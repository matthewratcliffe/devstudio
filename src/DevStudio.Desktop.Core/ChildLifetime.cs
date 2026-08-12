using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DevStudio.Desktop;

/// <summary>
/// Ties the server child to the shell, so a shell that dies badly does not leave a web server
/// holding the port. Windows has a kernel object for exactly this; elsewhere the best available
/// answer is to kill the child on the way out and let the process tree do the rest.
/// </summary>
internal interface IChildLifetime : IDisposable
{
    void Adopt(Process process);
}

internal static class ChildLifetime
{
    public static IChildLifetime Create() =>
        OperatingSystem.IsWindows() ? new WindowsJobObject() : new NoLifetime();

    private sealed class NoLifetime : IChildLifetime
    {
        public void Adopt(Process process)
        {
        }

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// A job object with kill-on-close: however the shell goes away — a clean exit, a crash, Task
    /// Manager — the server goes with it.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private sealed class WindowsJobObject : IChildLifetime
    {
        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JobObjectLimitKillOnJobClose = 0x2000;

        private readonly nint _handle;

        public WindowsJobObject()
        {
            _handle = CreateJobObject(nint.Zero, null);
            if (_handle == nint.Zero)
                return;

            var limits = new JobObjectExtendedLimit
            {
                BasicLimitInformation = new JobObjectBasicLimit { LimitFlags = JobObjectLimitKillOnJobClose },
            };

            var size = Marshal.SizeOf(limits);
            var buffer = Marshal.AllocHGlobal(size);

            try
            {
                Marshal.StructureToPtr(limits, buffer, false);
                SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, buffer, (uint)size);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public void Adopt(Process process)
        {
            if (_handle != nint.Zero)
                AssignProcessToJobObject(_handle, process.Handle);
        }

        public void Dispose()
        {
            if (_handle != nint.Zero)
                CloseHandle(_handle);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern nint CreateJobObject(nint attributes, string? name);

        [DllImport("kernel32.dll")]
        private static extern bool SetInformationJobObject(nint job, int infoClass, nint info, uint length);

        [DllImport("kernel32.dll")]
        private static extern bool AssignProcessToJobObject(nint job, nint process);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(nint handle);

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimit
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public nuint MinimumWorkingSetSize;
            public nuint MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public nuint Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimit
        {
            public JobObjectBasicLimit BasicLimitInformation;
            public IoCounters IoInfo;
            public nuint ProcessMemoryLimit;
            public nuint JobMemoryLimit;
            public nuint PeakProcessMemoryUsed;
            public nuint PeakJobMemoryUsed;
        }
    }
}
