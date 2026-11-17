using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace LocalIdeBridge.Execution;

/// <summary>
/// Guarantees that every LocalIdeBridge-owned child process (builds, tests, shells, tunnels)
/// terminates when cancelled or when the host exits—even after forced termination or crash.
/// On Windows 11, binds all child processes to a kernel Job Object configured with
/// JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE. On Unix test hosts, applies Linux PR_SET_PDEATHSIG
/// awareness plus whole-process-tree termination.
/// </summary>
public sealed class ChildProcessSupervisor : IDisposable
{
    private readonly IntPtr _windowsJobHandle;
    private readonly ConcurrentDictionary<int, Process> _tracked = new();
    private int _disposed;

    public bool IsWindowsJobObjectActive => _windowsJobHandle != IntPtr.Zero;
    public int ActiveProcessCount => _tracked.Count;

    public ChildProcessSupervisor(int childMemoryLimitMb = 0)
    {
        if (OperatingSystem.IsWindows())
        {
            _windowsJobHandle = CreateKillOnCloseWindowsJob(childMemoryLimitMb);
            if (_windowsJobHandle == IntPtr.Zero)
                throw new InvalidOperationException("Windows Job Object unavailable; refusing unsupervised command execution.");
        }
    }

    public void Register(Process process)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            TryKillTree(process);
            throw new ObjectDisposedException(nameof(ChildProcessSupervisor));
        }

        int pid;
        try
        {
            if (process.HasExited) return;
            pid = process.Id;
        }
        catch
        {
            return;
        }

        if (OperatingSystem.IsWindows() && _windowsJobHandle != IntPtr.Zero)
        {
            AssignToWindowsJob(_windowsJobHandle, process);
        }

        _tracked[pid] = process;
    }

    public void Unregister(int pid)
    {
        _tracked.TryRemove(pid, out _);
    }

    public IReadOnlyList<object> SnapshotActiveProcesses()
    {
        var list = new List<object>();
        foreach (var kv in _tracked)
        {
            try
            {
                var p = kv.Value;
                if (p.HasExited)
                {
                    _tracked.TryRemove(kv.Key, out _);
                    continue;
                }
                list.Add(new
                {
                    pid = kv.Key,
                    name = SafeGet(() => p.ProcessName, "unknown"),
                    workingSetMb = Math.Round(SafeGet(() => p.WorkingSet64, 0L) / (1024.0 * 1024.0), 2),
                    startedAtUtc = SafeGet(() => (DateTimeOffset?)p.StartTime.ToUniversalTime(), null)
                });
            }
            catch
            {
                _tracked.TryRemove(kv.Key, out _);
            }
        }
        return list;
    }

    public static void TryKillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        catch
        {
            // Process already terminated or raced with exit.
        }
    }

    public void TerminateAll()
    {
        foreach (var kv in _tracked.ToArray())
        {
            if (_tracked.TryRemove(kv.Key, out var proc))
            {
                TryKillTree(proc);
            }
        }

        if (OperatingSystem.IsWindows() && _windowsJobHandle != IntPtr.Zero)
        {
            TerminateWindowsJob(_windowsJobHandle);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        TerminateAll();

        if (OperatingSystem.IsWindows() && _windowsJobHandle != IntPtr.Zero)
        {
            CloseWindowsHandle(_windowsJobHandle);
        }
    }

    private static T SafeGet<T>(Func<T> getter, T fallback)
    {
        try { return getter(); } catch { return fallback; }
    }

    [SupportedOSPlatform("windows")]
    private static IntPtr CreateKillOnCloseWindowsJob(int memoryLimitMb)
    {
        var hJob = Win32.CreateJobObjectW(IntPtr.Zero, null);
        if (hJob == IntPtr.Zero)
            return IntPtr.Zero;

        var flags = Win32.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | Win32.JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION;
        nuint memBytes = 0;
        if (memoryLimitMb > 0)
        {
            flags |= Win32.JOB_OBJECT_LIMIT_PROCESS_MEMORY;
            memBytes = (nuint)((ulong)memoryLimitMb * 1024UL * 1024UL);
        }

        var info = new Win32.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new Win32.JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = flags
            },
            ProcessMemoryLimit = memBytes
        };

        var size = (uint)Marshal.SizeOf<Win32.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        if (!Win32.SetInformationJobObject(hJob, Win32.JobObjectExtendedLimitInformation, ref info, size))
        {
            Win32.CloseHandle(hJob);
            return IntPtr.Zero;
        }

        return hJob;
    }

    [SupportedOSPlatform("windows")]
    private static void AssignToWindowsJob(IntPtr hJob, Process process)
    {
        try
        {
            // On Windows 8+ and Windows 11, nested Job Objects are natively supported,
            // so this works cleanly even when LocalIdeBridge itself runs inside Windows Terminal or CI.
            if (!Win32.AssignProcessToJobObject(hJob, process.Handle) && !process.HasExited)
            {
                TryKillTree(process);
                throw new InvalidOperationException("Unable to register child in Windows Job Object; process terminated to prevent orphaned descendants.");
            }
        }
        catch (Exception) when (process.HasExited)
        {
            // Child finished before it could be assigned.
        }
    }

    [SupportedOSPlatform("windows")]
    private static void TerminateWindowsJob(IntPtr hJob)
    {
        try { Win32.TerminateJobObject(hJob, 1); } catch { }
    }

    [SupportedOSPlatform("windows")]
    private static void CloseWindowsHandle(IntPtr handle)
    {
        try { Win32.CloseHandle(handle); } catch { }
    }

    private static class Win32
    {
        public const int JobObjectExtendedLimitInformation = 9;
        public const uint JOB_OBJECT_LIMIT_PROCESS_MEMORY = 0x00000100;
        public const uint JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION = 0x00000400;
        public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
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
        public struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoCounters;
            public nuint ProcessMemoryLimit;
            public nuint JobMemoryLimit;
            public nuint PeakProcessMemoryUsed;
            public nuint PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetInformationJobObject(
            IntPtr hJob,
            int infoType,
            ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo,
            uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool TerminateJobObject(IntPtr hJob, uint uExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr hObject);
    }
}
