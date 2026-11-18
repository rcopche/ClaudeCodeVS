using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ClaudeCodeVS.Terminal
{
    /// <summary>Font and colors applied to the embedded console when it starts.</summary>
    internal sealed class ConsoleAppearance
    {
        public string FontName { get; set; }

        /// <summary>Character height in pixels; 0 keeps the console default.</summary>
        public int FontSize { get; set; }

        /// <summary>Default background as a COLORREF (0x00BBGGRR), or null for the console default.</summary>
        public int? BackgroundColor { get; set; }

        /// <summary>Default text color as a COLORREF (0x00BBGGRR), or null for the console default.</summary>
        public int? ForegroundColor { get; set; }
    }

    /// <summary>
    /// Hosts a real Windows console (conhost.exe) inside a WPF element by re-parenting
    /// the console window into a child HWND owned by Visual Studio.
    /// </summary>
    internal sealed class ConsoleHost : HwndHost, ITerminalHost
    {
        // Initial title of the console. conhost loads its settings (font, code page)
        // from HKCU\Console\<initial title>, so a title of our own gives this console
        // private settings without touching the user's other console windows.
        private const string ConsoleSettingsTitle = "Claude Code VS";

        private IntPtr _hostHwnd = IntPtr.Zero;
        private IntPtr _consoleHwnd = IntPtr.Zero;
        private Process _process;
        private KillOnCloseJob _job;
        private int _sessionId;

        /// <summary>Raised (on a thread-pool thread) when the console process exits on its own.</summary>
        public event EventHandler SessionExited;

        /// <summary>The classic console gives no way to observe the program's output.</summary>
        public bool IsBusy
        {
            get { return false; }
        }

        public FrameworkElement View
        {
            get { return this; }
        }

        /// <summary>The console is a window of another process; its keys never pass through Visual Studio.</summary>
        public bool PreProcessKeyMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
        {
            return false;
        }

        public bool IsRunning
        {
            get
            {
                Process process = _process;
                try
                {
                    return process != null && !process.HasExited;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            }
        }

        protected override HandleRef BuildWindowCore(HandleRef hwndParent)
        {
            int style = unchecked((int)(NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE | NativeMethods.WS_CLIPCHILDREN));
            _hostHwnd = NativeMethods.CreateWindowEx(
                0, "static", string.Empty, style,
                0, 0, 0, 0,
                hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

            return new HandleRef(this, _hostHwnd);
        }

        protected override void DestroyWindowCore(HandleRef hwnd)
        {
            Stop();
            NativeMethods.DestroyWindow(hwnd.Handle);
            _hostHwnd = IntPtr.Zero;
        }

        protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == NativeMethods.WM_SIZE)
            {
                ResizeConsole();
            }
            else if (msg == NativeMethods.WM_SETFOCUS)
            {
                // WPF focused the host window; the keyboard belongs to the console inside it.
                FocusConsole();
            }
            else if (msg == NativeMethods.WM_MOUSEACTIVATE)
            {
                FocusSoon();
            }
            else if (msg == NativeMethods.WM_PARENTNOTIFY)
            {
                // The console reports mouse clicks to its parent. As a child window it does
                // not take the keyboard focus on a click by itself, so it is given here.
                int mouseEvent = (int)(wParam.ToInt64() & 0xFFFF);
                if (mouseEvent == NativeMethods.WM_LBUTTONDOWN ||
                    mouseEvent == NativeMethods.WM_RBUTTONDOWN ||
                    mouseEvent == NativeMethods.WM_MBUTTONDOWN)
                {
                    FocusSoon();
                }
            }

            return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
        }

        /// <summary>Keyboard navigation into the host (Tab, tool window activation) goes to the console.</summary>
        protected override bool TabIntoCore(TraversalRequest request)
        {
            FocusConsole();
            return true;
        }

        /// <summary>Gives the keyboard focus to the embedded console right now.</summary>
        public void FocusConsole()
        {
            IntPtr console = _consoleHwnd;
            if (console != IntPtr.Zero && NativeMethods.IsWindow(console))
            {
                NativeMethods.SetFocus(console);
            }
        }

        /// <summary>
        /// Gives the keyboard focus to the console once the current input message has been
        /// handled, and makes Visual Studio treat this tool window as the active one.
        /// </summary>
        public void FocusSoon()
        {
            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    if (!IsKeyboardFocusWithin)
                    {
                        Focus();
                    }

                    FocusConsole();
                }),
                DispatcherPriority.Input);
        }

        protected override void OnWindowPositionChanged(Rect rcBoundingBox)
        {
            base.OnWindowPositionChanged(rcBoundingBox);
            ResizeConsole();
        }

        /// <summary>
        /// Starts a new console session running <paramref name="commandLine"/> in
        /// <paramref name="workingDirectory"/>. Any previous session is terminated first.
        /// Must be called on the UI thread.
        /// </summary>
        public async Task StartAsync(string workingDirectory, string commandLine, ConsoleAppearance appearance)
        {
            Stop();
            int session = ++_sessionId;

            // The host HWND is created when the element is connected to a window.
            for (int i = 0; i < 50 && _hostHwnd == IntPtr.Zero; i++)
            {
                await Task.Delay(100);
            }

            if (_hostHwnd == IntPtr.Zero || session != _sessionId)
            {
                throw new InvalidOperationException("The terminal host window is not available.");
            }

            // Launching conhost.exe explicitly forces the classic console window even when
            // Windows Terminal is the default terminal; that window can be re-parented.
            // "cmd /s /k" keeps the prompt open after the CLI exits, so errors such as
            // "'claude' is not recognized" stay visible.
            // GetWindowThreadProcessId reports a console window as owned by the client
            // process (cmd.exe), not by conhost.exe, so Process.MainWindowHandle never
            // finds it. The window is located by a unique title token instead.
            // "chcp 65001" switches the console to UTF-8 so the symbols and accented
            // characters printed by the CLI and by the tools it runs are not garbled.
            string token = "ClaudeCodeVS-" + Guid.NewGuid().ToString("N");
            var existing = new HashSet<IntPtr>(GetConsoleWindows());

            ApplyConsoleSettings(appearance);

            string conhost = Path.Combine(Environment.SystemDirectory, "conhost.exe");
            string arguments = "cmd.exe /s /k \"title " + token + "&chcp 65001>nul&" + commandLine + "\"";

            var job = new KillOnCloseJob();
            Process process;
            try
            {
                process = StartConsoleProcess(conhost, arguments, workingDirectory, job);
            }
            catch
            {
                job.Dispose();
                throw;
            }

            _job = job;
            _process = process;

            IntPtr consoleHwnd = IntPtr.Zero;
            bool exited = false;
            for (int i = 0; i < 150; i++)
            {
                if (session != _sessionId)
                {
                    return; // superseded by a newer Start/Stop
                }

                if (process.HasExited)
                {
                    exited = true;
                    break;
                }

                // After 3 seconds also accept any new visible console window, in case
                // the title was replaced before it could be matched.
                consoleHwnd = FindConsoleWindow(token, existing, job, i >= 30);
                if (consoleHwnd != IntPtr.Zero)
                {
                    break;
                }

                await Task.Delay(100);
            }

            if (session != _sessionId)
            {
                return;
            }

            if (consoleHwnd == IntPtr.Zero)
            {
                Stop();
                throw new InvalidOperationException(exited
                    ? "conhost.exe exited before its window could be attached."
                    : "The console window was not found.");
            }

            NativeMethods.ShowWindow(consoleHwnd, NativeMethods.SW_HIDE);

            long style = NativeMethods.GetWindowLongPtr(consoleHwnd, NativeMethods.GWL_STYLE).ToInt64();
            style &= ~(NativeMethods.WS_POPUP | NativeMethods.WS_CAPTION | NativeMethods.WS_THICKFRAME |
                       NativeMethods.WS_SYSMENU | NativeMethods.WS_MINIMIZEBOX | NativeMethods.WS_MAXIMIZEBOX);
            style |= NativeMethods.WS_CHILD;
            NativeMethods.SetWindowLongPtr(consoleHwnd, NativeMethods.GWL_STYLE, new IntPtr(style));
            NativeMethods.SetParent(consoleHwnd, _hostHwnd);
            if (NativeMethods.GetParent(consoleHwnd) != _hostHwnd)
            {
                // Report it instead of leaving a console floating outside Visual Studio.
                int error = Marshal.GetLastWin32Error();
                Stop();
                throw new InvalidOperationException("Could not attach the console to the tool window (error " + error + ").");
            }

            // Make sure the console tells its parent about mouse clicks (see WndProc).
            long exStyle = NativeMethods.GetWindowLongPtr(consoleHwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            if ((exStyle & NativeMethods.WS_EX_NOPARENTNOTIFY) != 0)
            {
                NativeMethods.SetWindowLongPtr(
                    consoleHwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle & ~NativeMethods.WS_EX_NOPARENTNOTIFY));
            }

            _consoleHwnd = consoleHwnd;
            ResizeConsole();
            NativeMethods.ShowWindow(consoleHwnd, NativeMethods.SW_SHOW);

            process.Exited += (sender, e) =>
            {
                if (session == _sessionId)
                {
                    EventHandler handler = SessionExited;
                    if (handler != null)
                    {
                        handler(this, EventArgs.Empty);
                    }
                }
            };
            process.EnableRaisingEvents = true;
        }

        /// <summary>
        /// Types <paramref name="text"/> into the console as if the user had typed it.
        /// Returns false when no console is attached.
        /// </summary>
        public bool SendText(string text)
        {
            IntPtr console = _consoleHwnd;
            if (console == IntPtr.Zero || string.IsNullOrEmpty(text) || !NativeMethods.IsWindow(console))
            {
                return false;
            }

            foreach (char ch in text)
            {
                NativeMethods.PostMessage(console, NativeMethods.WM_CHAR, new IntPtr(ch), IntPtr.Zero);
            }

            return true;
        }

        /// <summary>Terminates the current session, including every child process.</summary>
        public void Stop()
        {
            _sessionId++;
            _consoleHwnd = IntPtr.Zero;

            Process process = _process;
            _process = null;
            KillOnCloseJob job = _job;
            _job = null;

            // Closing the job kills conhost, cmd and the CLI together.
            if (job != null)
            {
                job.Dispose();
            }

            if (process != null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                    }
                }
                catch (InvalidOperationException)
                {
                }
                catch (System.ComponentModel.Win32Exception)
                {
                }

                process.Dispose();
            }
        }

        /// <summary>
        /// Writes the font and code page for this console to the registry before it starts.
        /// The default console font lacks the block characters the CLI draws with, which
        /// makes them misaligned; a font such as Cascadia Mono has them.
        /// </summary>
        private static void ApplyConsoleSettings(ConsoleAppearance appearance)
        {
            if (appearance == null)
            {
                return;
            }

            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey("Console\\" + ConsoleSettingsTitle))
                {
                    if (key == null)
                    {
                        return;
                    }

                    if (!string.IsNullOrWhiteSpace(appearance.FontName))
                    {
                        key.SetValue("FaceName", appearance.FontName.Trim(), RegistryValueKind.String);
                        key.SetValue("FontFamily", 0x36, RegistryValueKind.DWord); // TrueType, fixed pitch
                        key.SetValue("FontWeight", 400, RegistryValueKind.DWord);
                    }

                    if (appearance.FontSize > 0 && appearance.FontSize <= 200)
                    {
                        // High word: character height in pixels. Low word 0: width chosen by the font.
                        key.SetValue("FontSize", appearance.FontSize << 16, RegistryValueKind.DWord);
                    }

                    key.SetValue("CodePage", 65001, RegistryValueKind.DWord);

                    if (appearance.BackgroundColor.HasValue && appearance.ForegroundColor.HasValue)
                    {
                        // Palette entry 0 is the default background and entry 7 the default text.
                        key.SetValue("ColorTable00", appearance.BackgroundColor.Value, RegistryValueKind.DWord);
                        key.SetValue("ColorTable07", appearance.ForegroundColor.Value, RegistryValueKind.DWord);
                        key.SetValue("ScreenColors", 0x07, RegistryValueKind.DWord);
                    }
                    else
                    {
                        key.DeleteValue("ColorTable00", false);
                        key.DeleteValue("ColorTable07", false);
                        key.DeleteValue("ScreenColors", false);
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (System.Security.SecurityException)
            {
            }
            catch (IOException)
            {
            }
        }

        private static Process StartConsoleProcess(string conhost, string arguments, string workingDirectory, KillOnCloseJob job)
        {
            var startupInfo = new NativeMethods.STARTUPINFO();
            startupInfo.cb = Marshal.SizeOf(typeof(NativeMethods.STARTUPINFO));
            startupInfo.lpTitle = ConsoleSettingsTitle;

            var commandLine = new StringBuilder("\"" + conhost + "\" " + arguments);
            NativeMethods.PROCESS_INFORMATION info;
            if (!NativeMethods.CreateProcess(
                    conhost, commandLine, IntPtr.Zero, IntPtr.Zero, false, NativeMethods.CREATE_SUSPENDED,
                    IntPtr.Zero, workingDirectory, ref startupInfo, out info))
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                // Started suspended so that every process it creates is already in the job.
                job.Assign(info.hProcess);
                NativeMethods.ResumeThread(info.hThread);

                // The open process handle keeps the id from being reused meanwhile.
                return Process.GetProcessById(info.dwProcessId);
            }
            finally
            {
                NativeMethods.CloseHandle(info.hThread);
                NativeMethods.CloseHandle(info.hProcess);
            }
        }

        private static List<IntPtr> GetConsoleWindows()
        {
            var windows = new List<IntPtr>();
            var className = new StringBuilder(64);
            NativeMethods.EnumWindows(
                (hwnd, lParam) =>
                {
                    className.Length = 0;
                    NativeMethods.GetClassName(hwnd, className, className.Capacity);
                    if (className.ToString() == "ConsoleWindowClass")
                    {
                        windows.Add(hwnd);
                    }

                    return true;
                },
                IntPtr.Zero);
            return windows;
        }

        /// <summary>
        /// Finds the window of the console that was just started. The reliable test is the
        /// owning process: it must be one of the processes in this session's job. The title
        /// token and "any new console window" are fallbacks.
        /// </summary>
        private static IntPtr FindConsoleWindow(string token, HashSet<IntPtr> existing, KillOnCloseJob job, bool acceptAnyNew)
        {
            HashSet<int> sessionProcesses = job.GetProcessIds();
            IntPtr byTitle = IntPtr.Zero;
            IntPtr fallback = IntPtr.Zero;
            var title = new StringBuilder(512);
            foreach (IntPtr hwnd in GetConsoleWindows())
            {
                if (existing.Contains(hwnd))
                {
                    continue;
                }

                uint processId;
                NativeMethods.GetWindowThreadProcessId(hwnd, out processId);
                if (sessionProcesses.Contains((int)processId))
                {
                    return hwnd;
                }

                title.Length = 0;
                NativeMethods.GetWindowText(hwnd, title, title.Capacity);
                if (byTitle == IntPtr.Zero &&
                    title.ToString().IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    byTitle = hwnd;
                }

                if (acceptAnyNew && fallback == IntPtr.Zero && NativeMethods.IsWindowVisible(hwnd))
                {
                    fallback = hwnd;
                }
            }

            return byTitle != IntPtr.Zero ? byTitle : fallback;
        }

        private void ResizeConsole()
        {
            IntPtr console = _consoleHwnd;
            if (console == IntPtr.Zero || _hostHwnd == IntPtr.Zero || !NativeMethods.IsWindow(console))
            {
                return;
            }

            NativeMethods.RECT rect;
            if (NativeMethods.GetClientRect(_hostHwnd, out rect))
            {
                NativeMethods.SetWindowPos(
                    console, IntPtr.Zero, 0, 0, rect.Right - rect.Left, rect.Bottom - rect.Top,
                    NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);
            }
        }
    }
}
