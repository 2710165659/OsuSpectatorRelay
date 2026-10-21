using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;

namespace OsuSpectatorRelay.Hosting;

public sealed class GameLifetime(string executablePath)
{
    private readonly ConcurrentDictionary<int, DateTime> processes = new();
    private int registered;
    public bool HasRegisteredGame => Volatile.Read(ref registered) != 0;

    public void Register(int pid)
    {
        // A bootstrapper can exit before IPC arrives. Still look for the actual
        // game at the configured path before deciding that the session ended.
        Interlocked.Exchange(ref registered, 1);
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.HasExited) processes[pid] = process.StartTime;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception) { }
    }

    public bool HasRunningGame()
    {
        foreach (var entry in processes)
        {
            try
            {
                using var process = Process.GetProcessById(entry.Key);
                if (!process.HasExited && process.StartTime == entry.Value) continue;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception) { }
            processes.TryRemove(entry.Key, out _);
        }
        if (!processes.IsEmpty) return true;
        if (!HasRegisteredGame || string.IsNullOrWhiteSpace(executablePath)) return false;
        string expected = Path.GetFullPath(executablePath);
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(expected)))
        {
            using (process)
            {
                try
                {
                    if (!process.HasExited && string.Equals(process.MainModule?.FileName, expected, StringComparison.OrdinalIgnoreCase))
                        processes[process.Id] = process.StartTime;
                }
                catch (Exception e) when (e is InvalidOperationException or Win32Exception) { }
            }
        }
        return !processes.IsEmpty;
    }
}
