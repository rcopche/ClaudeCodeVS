using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClaudeCodeVS.Terminal
{
    /// <summary>
    /// A Windows job object that terminates every process assigned to it (and their
    /// children) when the job handle is closed. This guarantees the console and the
    /// Claude Code CLI never outlive the session, even if Visual Studio crashes.
    /// </summary>
    internal sealed class KillOnCloseJob : IDisposable
    {
        private IntPtr _handle;

        public KillOnCloseJob()
        {
            _handle = NativeMethods.CreateJobObject(IntPtr.Zero, null);
            if (_handle == IntPtr.Zero)
            {
                return;
            }

            var info = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = NativeMethods.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;

            int length = Marshal.SizeOf(typeof(NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
            IntPtr buffer = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(info, buffer, false);
                NativeMethods.SetInformationJobObject(
                    _handle, NativeMethods.JobObjectExtendedLimitInformation, buffer, (uint)length);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public void Assign(Process process)
        {
            if (_handle != IntPtr.Zero && process != null)
            {
                NativeMethods.AssignProcessToJobObject(_handle, process.Handle);
            }
        }

        public void Assign(IntPtr processHandle)
        {
            if (_handle != IntPtr.Zero && processHandle != IntPtr.Zero)
            {
                NativeMethods.AssignProcessToJobObject(_handle, processHandle);
            }
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(_handle);
                _handle = IntPtr.Zero;
            }
        }
    }
}
