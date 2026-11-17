using System;
using System.Collections.Generic;
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

        /// <summary>Ids of the processes currently in the job (the console host and everything it started).</summary>
        public HashSet<int> GetProcessIds()
        {
            var ids = new HashSet<int>();
            if (_handle == IntPtr.Zero)
            {
                return ids;
            }

            // JOBOBJECT_BASIC_PROCESS_ID_LIST: two DWORDs, then one pointer-sized id per process.
            const int MaxProcesses = 256;
            int length = 8 + IntPtr.Size * MaxProcesses;
            IntPtr buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (NativeMethods.QueryInformationJobObject(
                        _handle, NativeMethods.JobObjectBasicProcessIdList, buffer, (uint)length, IntPtr.Zero))
                {
                    int count = Math.Min(Marshal.ReadInt32(buffer, 4), MaxProcesses);
                    for (int i = 0; i < count; i++)
                    {
                        ids.Add((int)Marshal.ReadIntPtr(buffer, 8 + IntPtr.Size * i).ToInt64());
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            return ids;
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
