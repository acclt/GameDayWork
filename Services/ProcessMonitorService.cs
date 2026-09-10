using System.Diagnostics;
using System.Runtime.InteropServices;
using GameOrchestrator.Models;

namespace GameOrchestrator.Services;

public sealed class ProcessMonitorService
{
    public IReadOnlyList<TrackedProcess> Scan(RuntimeSession session, AutomationTaskConfig task)
    {
        var parents = SnapshotParents();
        var descendants = task.TrackChildren ? DescendantsOf(session.RootPid, parents) : new HashSet<int>();
        var results = new Dictionary<int, TrackedProcess>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var pid = process.Id;
                var path = TryPath(process);
                var start = TryStart(process);
                var parent = parents.TryGetValue(pid, out var ppid) ? ppid : (int?)null;
                var source = pid == session.RootPid ? TrackedProcessSource.Root : descendants.Contains(pid) ? TrackedProcessSource.Child : TrackedProcessSource.RuleMatched;
                var owned = pid == session.RootPid || descendants.Contains(pid) || MatchesSafeRule(process.ProcessName, path, start, session, task.ProcessRules);
                if (owned) results[pid] = new(pid, process.ProcessName, path, start, parent, source);
            }
            catch { }
            finally { process.Dispose(); }
        }
        session.TrackedProcesses.Clear(); session.TrackedProcesses.AddRange(results.Values);
        return [.. results.Values];
    }

    private static bool MatchesSafeRule(string processName, string? path, DateTimeOffset? start, RuntimeSession session, IEnumerable<ProcessRule> rules)
    {
        foreach (var rule in rules.Where(r => r.Monitor || r.Cleanup))
        {
            if (!string.IsNullOrWhiteSpace(rule.ExecutablePath) && path is not null && string.Equals(Path.GetFullPath(path), Path.GetFullPath(rule.ExecutablePath), StringComparison.OrdinalIgnoreCase)) return start is null || start >= session.StartTime.AddSeconds(-2);
            if (rule.AllowNameFallback && !string.IsNullOrWhiteSpace(rule.ProcessName) && string.Equals(Path.GetFileNameWithoutExtension(rule.ProcessName), processName, StringComparison.OrdinalIgnoreCase)) return start is null || start >= session.StartTime.AddSeconds(-2);
        }
        return false;
    }

    private static string? TryPath(Process p) { try { return p.MainModule?.FileName; } catch { return null; } }
    private static DateTimeOffset? TryStart(Process p) { try { return p.StartTime; } catch { return null; } }
    private static HashSet<int> DescendantsOf(int root, Dictionary<int, int> parents)
    {
        var found = new HashSet<int>(); bool changed;
        do { changed = false; foreach (var pair in parents) if ((pair.Value == root || found.Contains(pair.Value)) && found.Add(pair.Key)) changed = true; } while (changed);
        return found;
    }
    private static Dictionary<int, int> SnapshotParents()
    {
        var result = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new IntPtr(-1)) return result;
        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (Process32First(snapshot, ref entry)) do { result[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID; } while (Process32Next(snapshot, ref entry));
        }
        finally { CloseHandle(snapshot); }
        return result;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct PROCESSENTRY32 { public uint dwSize, cntUsage, th32ProcessID; public UIntPtr th32DefaultHeapID; public uint th32ModuleID, cntThreads, th32ParentProcessID; public int pcPriClassBase; public uint dwFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile; }
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32First(IntPtr snapshot, ref PROCESSENTRY32 entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32Next(IntPtr snapshot, ref PROCESSENTRY32 entry);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
}
