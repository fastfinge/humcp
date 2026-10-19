using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Humcp.App;

/// <summary>
/// Puts humcp in a Windows job object that ends every process inside it when humcp ends — however
/// it ends: closed, crashed, or killed from Task Manager (ROADMAP §10, Phase 7).
/// </summary>
/// <remarks>
/// <para>
/// Every stdio server humcp starts is a child process, and an <c>npx</c> server is a grandchild as
/// well, a <c>node</c> started by <c>npx</c>. Stopping them politely on close is not enough: a crash or
/// a kill gives humcp no chance to, and found in Phase 7, a test run that closed the app left three
/// fixture servers running for twenty minutes. A child started after this call inherits the job, so
/// the operating system does the cleaning up, and nothing depends on humcp being alive to do it.
/// </para>
/// <para>
/// Failure is not fatal: a humcp that cannot make a job (an unusual policy, an old Windows) runs
/// exactly as it did before Phase 7, and says so in the speech diagnostics rather than refusing to
/// start.
/// </para>
/// </remarks>
internal static class ChildProcessJob
{
    private static IntPtr _job;

    /// <summary>Why the job could not be made, or null when it was.</summary>
    public static string? Problem { get; private set; }

    public static void Enter()
    {
        if (_job != IntPtr.Zero) return;

        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
        {
            Problem = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return;
        }

        var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
            },
        };

        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var buffer = Marshal.AllocHGlobal(size);

        try
        {
            Marshal.StructureToPtr(limits, buffer, false);

            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, (uint)size)
                || !AssignProcessToJobObject(job, GetCurrentProcess()))
            {
                Problem = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                CloseHandle(job);
                return;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        // Deliberately never closed: the handle closing is what ends the children, and it closes
        // when the process does.
        _job = job;
    }

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
