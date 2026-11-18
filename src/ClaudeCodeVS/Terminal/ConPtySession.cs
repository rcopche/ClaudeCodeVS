using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace ClaudeCodeVS.Terminal
{
    /// <summary>
    /// A process attached to a Windows pseudo console (ConPTY): everything it prints arrives
    /// as VT text through <see cref="Output"/>, and <see cref="Write"/> feeds its keyboard.
    /// Requires Windows 10 version 1809 or later.
    /// </summary>
    internal sealed class ConPtySession : IDisposable
    {
        private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
        private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        private const int STARTF_USESTDHANDLES = 0x00000100;
        private static readonly IntPtr PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = (IntPtr)0x00020016;

        private readonly object _writeLock = new object();
        private IntPtr _pseudoConsole = IntPtr.Zero;
        private FileStream _input;
        private FileStream _output;
        private Process _process;
        private KillOnCloseJob _job;
        private volatile bool _disposed;

        /// <summary>Raised on a background thread with the text printed by the process.</summary>
        public event Action<string> Output;

        /// <summary>Raised on a background thread when the process exits.</summary>
        public event EventHandler Exited;

        public bool HasExited
        {
            get
            {
                Process process = _process;
                try
                {
                    return _disposed || process == null || process.HasExited;
                }
                catch (InvalidOperationException)
                {
                    return true;
                }
            }
        }

        /// <summary>Starts the process. On failure the caller must dispose the session.</summary>
        public void Start(string commandLine, string workingDirectory, int columns, int rows)
        {
            SafeFileHandle inputRead = null;
            SafeFileHandle inputWrite = null;
            SafeFileHandle outputRead = null;
            SafeFileHandle outputWrite = null;
            IntPtr attributeList = IntPtr.Zero;
            bool attributeListReady = false;

            try
            {
                if (!CreatePipe(out inputRead, out inputWrite, IntPtr.Zero, 0) ||
                    !CreatePipe(out outputRead, out outputWrite, IntPtr.Zero, 0))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                int result = CreatePseudoConsole(ToSize(columns, rows), inputRead, outputWrite, 0, out _pseudoConsole);
                if (result != 0)
                {
                    Marshal.ThrowExceptionForHR(result);
                }

                // The attribute list is what attaches the new process to the pseudo console.
                IntPtr listSize = IntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref listSize);
                attributeList = Marshal.AllocHGlobal(listSize);
                if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref listSize))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                attributeListReady = true;
                if (!UpdateProcThreadAttribute(
                        attributeList, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, _pseudoConsole,
                        (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                var startup = new STARTUPINFOEX();
                startup.StartupInfo.cb = Marshal.SizeOf(typeof(STARTUPINFOEX));
                // Without this flag the child could pick up standard handles of this process
                // instead of the pseudo console.
                startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
                startup.lpAttributeList = attributeList;

                var job = new KillOnCloseJob();
                NativeMethods.PROCESS_INFORMATION info;
                if (!CreateProcess(
                        null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false,
                        EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT | NativeMethods.CREATE_SUSPENDED,
                        IntPtr.Zero, workingDirectory, ref startup, out info))
                {
                    int error = Marshal.GetLastWin32Error();
                    job.Dispose();
                    throw new Win32Exception(error);
                }

                _job = job;
                try
                {
                    // Started suspended so that every process it creates is already in the job.
                    job.Assign(info.hProcess);
                    NativeMethods.ResumeThread(info.hThread);
                    _process = Process.GetProcessById(info.dwProcessId);
                }
                finally
                {
                    NativeMethods.CloseHandle(info.hThread);
                    NativeMethods.CloseHandle(info.hProcess);
                }

                _input = new FileStream(inputWrite, FileAccess.Write, 256, false);
                inputWrite = null;
                _output = new FileStream(outputRead, FileAccess.Read, 4096, false);
                outputRead = null;
            }
            finally
            {
                // The pseudo console keeps its own copies of these two pipe ends.
                if (inputRead != null)
                {
                    inputRead.Dispose();
                }

                if (outputWrite != null)
                {
                    outputWrite.Dispose();
                }

                // Only still set when something above failed.
                if (inputWrite != null)
                {
                    inputWrite.Dispose();
                }

                if (outputRead != null)
                {
                    outputRead.Dispose();
                }

                if (attributeListReady)
                {
                    DeleteProcThreadAttributeList(attributeList);
                }

                if (attributeList != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(attributeList);
                }
            }

            _process.Exited += OnProcessExited;
            _process.EnableRaisingEvents = true;

            var reader = new Thread(ReadLoop);
            reader.IsBackground = true;
            reader.Name = "ClaudeCodeVS terminal output";
            reader.Start();
        }

        /// <summary>Sends text to the process as keyboard input.</summary>
        public void Write(string text)
        {
            if (string.IsNullOrEmpty(text) || _disposed)
            {
                return;
            }

            byte[] bytes = Encoding.UTF8.GetBytes(text);
            lock (_writeLock)
            {
                FileStream input = _input;
                if (input == null)
                {
                    return;
                }

                try
                {
                    input.Write(bytes, 0, bytes.Length);
                    input.Flush();
                }
                catch (IOException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        public void Resize(int columns, int rows)
        {
            IntPtr pseudoConsole = _pseudoConsole;
            if (pseudoConsole != IntPtr.Zero && !_disposed)
            {
                ResizePseudoConsole(pseudoConsole, ToSize(columns, rows));
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            // Closing the job terminates the shell and everything it started.
            KillOnCloseJob job = _job;
            _job = null;
            if (job != null)
            {
                job.Dispose();
            }

            IntPtr pseudoConsole = _pseudoConsole;
            _pseudoConsole = IntPtr.Zero;
            FileStream input;
            lock (_writeLock)
            {
                input = _input;
                _input = null;
            }

            FileStream output = _output;
            Process process = _process;

            // ClosePseudoConsole can wait for the remaining output to be read, so it runs
            // off the UI thread while the reader thread keeps draining the pipe.
            ThreadPool.QueueUserWorkItem(state =>
            {
                try
                {
                    if (pseudoConsole != IntPtr.Zero)
                    {
                        ClosePseudoConsole(pseudoConsole);
                    }
                }
                catch (Exception)
                {
                }

                try
                {
                    if (input != null)
                    {
                        input.Dispose();
                    }

                    if (output != null)
                    {
                        output.Dispose();
                    }

                    if (process != null)
                    {
                        process.Dispose();
                    }
                }
                catch (Exception)
                {
                }
            });
        }

        private void OnProcessExited(object sender, EventArgs e)
        {
            if (_disposed)
            {
                return;
            }

            EventHandler handler = Exited;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private void ReadLoop()
        {
            FileStream output = _output;
            var bytes = new byte[8192];
            var chars = new char[8192 + 16];
            // A decoder keeps the bytes of a character that was split between two reads.
            Decoder decoder = Encoding.UTF8.GetDecoder();

            try
            {
                while (true)
                {
                    int read = output.Read(bytes, 0, bytes.Length);
                    if (read <= 0)
                    {
                        break;
                    }

                    int count = decoder.GetChars(bytes, 0, read, chars, 0);
                    if (count > 0 && !_disposed)
                    {
                        Action<string> handler = Output;
                        if (handler != null)
                        {
                            handler(new string(chars, 0, count));
                        }
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private static COORD ToSize(int columns, int rows)
        {
            return new COORD
            {
                X = (short)Math.Max(2, Math.Min(columns, 1000)),
                Y = (short)Math.Max(2, Math.Min(rows, 1000))
            };
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct COORD
        {
            public short X;
            public short Y;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFOEX
        {
            public NativeMethods.STARTUPINFO StartupInfo;
            public IntPtr lpAttributeList;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreatePipe(
            out SafeFileHandle readPipe, out SafeFileHandle writePipe, IntPtr pipeAttributes, int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int CreatePseudoConsole(
            COORD size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr pseudoConsole);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int ResizePseudoConsole(IntPtr pseudoConsole, COORD size);

        [DllImport("kernel32.dll")]
        private static extern void ClosePseudoConsole(IntPtr pseudoConsole);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool InitializeProcThreadAttributeList(
            IntPtr attributeList, int attributeCount, int flags, ref IntPtr size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UpdateProcThreadAttribute(
            IntPtr attributeList, uint flags, IntPtr attribute, IntPtr value, IntPtr size,
            IntPtr previousValue, IntPtr returnSize);

        [DllImport("kernel32.dll")]
        private static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateProcessW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateProcess(
            string applicationName, StringBuilder commandLine,
            IntPtr processAttributes, IntPtr threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags,
            IntPtr environment, string currentDirectory,
            ref STARTUPINFOEX startupInfo, out NativeMethods.PROCESS_INFORMATION processInformation);
    }
}
