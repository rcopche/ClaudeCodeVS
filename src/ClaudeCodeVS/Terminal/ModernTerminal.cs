using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Terminal.Wpf;

namespace ClaudeCodeVS.Terminal
{
    /// <summary>
    /// Terminal built on the control Visual Studio uses for its own terminal window
    /// (Microsoft.Terminal.Wpf, the Windows Terminal engine), fed by a pseudo console.
    /// Compared with the classic console it scrolls correctly, takes keyboard focus like
    /// any other control, supports paste and reports modified keys such as Shift+Enter.
    /// </summary>
    internal sealed class ModernTerminal : ITerminalHost
    {
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_CHAR = 0x0102;

        private const int VK_TAB = 0x09;
        private const int VK_RETURN = 0x0D;
        private const int VK_SHIFT = 0x10;
        private const int VK_CONTROL = 0x11;
        private const int VK_MENU = 0x12;
        private const int VK_INSERT = 0x2D;
        private const int VK_F4 = 0x73;

        private static readonly string Escape = ((char)27).ToString();

        private readonly TerminalControl _control;
        private readonly Connection _connection = new Connection();
        private HwndHost _terminalHost;
        private IntPtr _terminalHwnd = IntPtr.Zero;
        private ConPtySession _session;
        private int _sessionId;
        private volatile bool _bracketedPaste;
        private volatile int _lastOutputTick;
        private volatile int _lastInputTick;

        public ModernTerminal()
        {
            LoadNativeLibrary();

            _control = new TerminalControl();
            _control.AutoResize = true;

            _connection.Input += OnTerminalInput;
            _connection.Resized += OnTerminalResized;
        }

        public event EventHandler SessionExited;

        public FrameworkElement View
        {
            get { return _control; }
        }

        public bool IsRunning
        {
            get
            {
                ConPtySession session = _session;
                return session != null && !session.HasExited;
            }
        }

        public bool IsBusy
        {
            get
            {
                if (!IsRunning)
                {
                    return false;
                }

                // While Claude works it redraws its progress line at least once a second.
                // Output that arrives right after a key press is just the echo of typing.
                int now = Environment.TickCount;
                return unchecked(now - _lastOutputTick) < 1500 && unchecked(now - _lastInputTick) > 800;
            }
        }

        public async Task StartAsync(string workingDirectory, string commandLine, ConsoleAppearance appearance)
        {
            Stop();
            int sessionId = ++_sessionId;

            // The native terminal exists once the control is in a window.
            for (int i = 0; i < 50 && !_control.IsLoaded; i++)
            {
                await Task.Delay(100);
            }

            if (!_control.IsLoaded || sessionId != _sessionId)
            {
                throw new InvalidOperationException("The terminal is not available.");
            }

            _terminalHost = FindHwndHost(_control);
            _terminalHwnd = _terminalHost != null ? _terminalHost.Handle : IntPtr.Zero;

            ApplyTheme(appearance);

            // Assigning the connection (again) also resets the screen for the new session.
            _control.Connection = _connection;

            int columns = _control.Columns > 1 ? _control.Columns : 120;
            int rows = _control.Rows > 1 ? _control.Rows : 30;

            var session = new ConPtySession();
            session.Output += data =>
            {
                if (sessionId == _sessionId)
                {
                    _lastOutputTick = Environment.TickCount;
                    TrackModes(data);
                    _connection.Send(data);
                }
            };
            session.Exited += (sender, e) =>
            {
                if (sessionId == _sessionId)
                {
                    EventHandler handler = SessionExited;
                    if (handler != null)
                    {
                        handler(this, EventArgs.Empty);
                    }
                }
            };

            // "cmd /s /k" resolves .cmd shims, runs "a && b" chains and keeps the prompt
            // open after the CLI exits, so errors stay visible.
            string shell = "\"" + Path.Combine(Environment.SystemDirectory, "cmd.exe") + "\"" +
                           " /s /k \"chcp 65001>nul&" + commandLine + "\"";
            try
            {
                session.Start(shell, workingDirectory, columns, rows);
            }
            catch
            {
                session.Dispose();
                throw;
            }

            _session = session;
        }

        public void Stop()
        {
            _sessionId++;
            _bracketedPaste = false;

            ConPtySession session = _session;
            _session = null;
            if (session != null)
            {
                session.Dispose();
            }
        }

        public bool SendText(string text)
        {
            ConPtySession session = _session;
            if (session == null || string.IsNullOrEmpty(text))
            {
                return false;
            }

            _lastInputTick = Environment.TickCount;
            session.Write(text);
            return true;
        }

        public void FocusSoon()
        {
            _control.Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    HwndHost host = _terminalHost;
                    if (host != null)
                    {
                        host.Focus();
                        if (_terminalHwnd != IntPtr.Zero)
                        {
                            NativeMethods.SetFocus(_terminalHwnd);
                        }
                    }
                }),
                DispatcherPriority.Input);
        }

        /// <summary>
        /// Called for every keyboard message while the tool window is active. Keys aimed at
        /// the terminal are delivered to it here, before Visual Studio can treat them as its
        /// own shortcuts (Esc, Ctrl+V, Backspace...). Alt combinations and the window
        /// switching keys are left to Visual Studio.
        /// </summary>
        public bool PreProcessKeyMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
        {
            ConPtySession session = _session;
            if (session == null || _terminalHwnd == IntPtr.Zero)
            {
                return false;
            }

            if (msg != WM_KEYDOWN && msg != WM_KEYUP && msg != WM_CHAR)
            {
                return false;
            }

            if (hwnd != _terminalHwnd && !IsChild(_terminalHwnd, hwnd))
            {
                return false;
            }

            int key = (int)(wParam.ToInt64() & 0xFFFF);
            bool control = IsDown(VK_CONTROL);
            bool shift = IsDown(VK_SHIFT);
            bool alt = IsDown(VK_MENU);

            if (msg == WM_KEYDOWN)
            {
                if (control && !alt && (key == VK_TAB || key == VK_F4))
                {
                    return false;
                }

                if (key == VK_RETURN && shift && !control && !alt)
                {
                    // Shift+Enter: new line in the prompt. Claude Code takes a line feed
                    // (what Ctrl+J sends) as "insert a line break" in any terminal.
                    _lastInputTick = Environment.TickCount;
                    session.Write("\n");
                    return true;
                }

                bool pasteKey = (control && !alt && key == 'V') || (shift && !control && !alt && key == VK_INSERT);
                if (pasteKey)
                {
                    Paste(session);
                    return true;
                }

                if (control && !alt && !shift && key == 'C' && CopySelection())
                {
                    return true;
                }
            }

            var message = new MSG { hwnd = hwnd, message = (uint)msg, wParam = wParam, lParam = lParam };
            TranslateMessage(ref message);
            DispatchMessage(ref message);
            return true;
        }

        public void Dispose()
        {
            Stop();
            _connection.Input -= OnTerminalInput;
            _connection.Resized -= OnTerminalResized;
        }

        private void OnTerminalInput(string data)
        {
            ConPtySession session = _session;
            if (session != null)
            {
                _lastInputTick = Environment.TickCount;
                session.Write(data);
            }
        }

        private void OnTerminalResized(int rows, int columns)
        {
            ConPtySession session = _session;
            if (session != null)
            {
                session.Resize(columns, rows);
            }
        }

        private void Paste(ConPtySession session)
        {
            string text;
            try
            {
                text = Clipboard.ContainsText() ? Clipboard.GetText() : null;
            }
            catch (COMException)
            {
                return; // the clipboard is busy
            }

            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            // A terminal sends Enter as a carriage return.
            text = text.Replace("\r\n", "\r").Replace("\n", "\r");
            if (_bracketedPaste)
            {
                // The program asked for pasted text to be marked, so that line breaks in
                // it are not taken as Enter.
                text = Escape + "[200~" + text + Escape + "[201~";
            }

            _lastInputTick = Environment.TickCount;
            session.Write(text);
        }

        /// <summary>Copies the selected text, if any. Ctrl+C without a selection still interrupts.</summary>
        private bool CopySelection()
        {
            try
            {
                string selection = _control.GetSelectedText();
                if (string.IsNullOrEmpty(selection))
                {
                    return false;
                }

                Clipboard.SetText(selection);
                return true;
            }
            catch (COMException)
            {
                return false;
            }
        }

        /// <summary>Follows whether the program turned bracketed paste on or off.</summary>
        private void TrackModes(string data)
        {
            int on = data.LastIndexOf(Escape + "[?2004h", StringComparison.Ordinal);
            int off = data.LastIndexOf(Escape + "[?2004l", StringComparison.Ordinal);
            if (on > off)
            {
                _bracketedPaste = true;
            }
            else if (off > on)
            {
                _bracketedPaste = false;
            }
        }

        private void ApplyTheme(ConsoleAppearance appearance)
        {
            uint background = appearance != null && appearance.BackgroundColor.HasValue
                ? (uint)appearance.BackgroundColor.Value
                : Rgb(12, 12, 12);
            uint foreground = appearance != null && appearance.ForegroundColor.HasValue
                ? (uint)appearance.ForegroundColor.Value
                : Rgb(204, 204, 204);

            var theme = new TerminalTheme
            {
                DefaultBackground = background,
                DefaultForeground = foreground,
                DefaultSelectionBackground = Rgb(38, 79, 120),
                CursorStyle = CursorStyle.BlinkingBar,
                // The Windows Terminal "Campbell" palette, as 0x00BBGGRR values.
                ColorTable = new[]
                {
                    Rgb(12, 12, 12), Rgb(197, 15, 31), Rgb(19, 161, 14), Rgb(193, 156, 0),
                    Rgb(0, 55, 218), Rgb(136, 23, 152), Rgb(58, 150, 221), Rgb(204, 204, 204),
                    Rgb(118, 118, 118), Rgb(231, 72, 86), Rgb(22, 198, 12), Rgb(249, 241, 165),
                    Rgb(59, 120, 255), Rgb(180, 0, 158), Rgb(97, 214, 214), Rgb(242, 242, 242)
                }
            };

            string fontName = appearance != null && !string.IsNullOrWhiteSpace(appearance.FontName)
                ? appearance.FontName
                : "Cascadia Mono";

            // The option is a height in pixels (classic console); this control takes points.
            int pixels = appearance != null && appearance.FontSize > 0 ? appearance.FontSize : 16;
            short points = (short)Math.Max(7, Math.Min(48, (int)Math.Round(pixels * 0.75)));

            Color surround = Color.FromRgb(
                (byte)(background & 0xFF), (byte)((background >> 8) & 0xFF), (byte)((background >> 16) & 0xFF));

            _control.SetTheme(theme, fontName, points, surround);
        }

        private static uint Rgb(int red, int green, int blue)
        {
            return (uint)(red | (green << 8) | (blue << 16));
        }

        private static HwndHost FindHwndHost(DependencyObject root)
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                var host = child as HwndHost;
                if (host != null)
                {
                    return host;
                }

                host = FindHwndHost(child);
                if (host != null)
                {
                    return host;
                }
            }

            return null;
        }

        /// <summary>
        /// The control calls into Microsoft.Terminal.Control.dll, which sits next to its
        /// assembly and not on the default search path. Loading it by full path first makes
        /// the later calls find it.
        /// </summary>
        private static void LoadNativeLibrary()
        {
            string folder = Path.GetDirectoryName(typeof(TerminalControl).Assembly.Location);
            if (string.IsNullOrEmpty(folder))
            {
                return;
            }

            string native = Path.Combine(folder, "Microsoft.Terminal.Control.dll");
            if (File.Exists(native))
            {
                LoadLibrary(native);
            }
        }

        private static bool IsDown(int virtualKey)
        {
            return (GetKeyState(virtualKey) & 0x8000) != 0;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int x;
            public int y;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TranslateMessage(ref MSG message);

        [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
        private static extern IntPtr DispatchMessage(ref MSG message);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsChild(IntPtr parent, IntPtr window);

        [DllImport("user32.dll")]
        private static extern short GetKeyState(int virtualKey);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "LoadLibraryW", SetLastError = true)]
        private static extern IntPtr LoadLibrary(string fileName);

        /// <summary>Bridges the terminal control and the pseudo console session.</summary>
        private sealed class Connection : ITerminalConnection
        {
            public event EventHandler<TerminalOutputEventArgs> TerminalOutput;

            /// <summary>Keys typed in the terminal, already encoded for the program.</summary>
            public event Action<string> Input;

            /// <summary>New size in rows and columns.</summary>
            public event Action<int, int> Resized;

            public void Start()
            {
                // The session is started by ModernTerminal.StartAsync.
            }

            public void WriteInput(string data)
            {
                Action<string> handler = Input;
                if (handler != null)
                {
                    handler(data);
                }
            }

            public void Resize(uint rows, uint columns)
            {
                Action<int, int> handler = Resized;
                if (handler != null)
                {
                    handler((int)rows, (int)columns);
                }
            }

            public void Close()
            {
            }

            /// <summary>Shows text printed by the program.</summary>
            public void Send(string data)
            {
                EventHandler<TerminalOutputEventArgs> handler = TerminalOutput;
                if (handler != null)
                {
                    handler(this, new TerminalOutputEventArgs(data));
                }
            }
        }
    }
}
