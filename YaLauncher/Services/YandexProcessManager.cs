using System.Diagnostics;

namespace YaLauncher.Services;

internal sealed class YandexProcessManager
{
    private static readonly string[] CandidateNames =
    {
        "Яндекс Музыка",
        "Yandex Music",
        "YandexMusic",
        "Yandex.Music"
    };

    public IReadOnlyList<int> GetRunningPids()
    {
        var processes = Process.GetProcesses();
        var pids = new HashSet<int>();

        foreach (var process in processes)
        {
            try
            {
                if (IsTargetProcess(process))
                    pids.Add(process.Id);
            }
            catch
            {
                // ignore inaccessible process details
            }
            finally
            {
                process.Dispose();
            }
        }

        return pids.ToArray();
    }

    public async Task StopAllAsync(CancellationToken ct = default)
    {
        var firstPass = GetRunningPids();
        foreach (var pid in firstPass)
            TryKillProcessTree(pid);

        await Task.Delay(300, ct);
        var secondPass = GetRunningPids();
        foreach (var pid in secondPass)
            await TryTaskKillAsync(pid, ct);

        await Task.Delay(300, ct);
        var stillRunning = GetRunningPids();
        if (stillRunning.Count > 0)
            throw new InvalidOperationException($"Не удалось завершить процессы Яндекс.Музыки: {string.Join(", ", stillRunning)}");
    }

    private static bool IsTargetProcess(Process process)
    {
        var name = process.ProcessName.Trim();
        return CandidateNames.Any(candidate =>
            string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase));
    }

    private static void TryKillProcessTree(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // ignore
        }
    }

    private static async Task TryTaskKillAsync(int pid, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("taskkill")
            {
                Arguments = $"/PID {pid} /T /F",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            if (process == null)
                return;

            await process.WaitForExitAsync(ct);
        }
        catch
        {
            // ignore
        }
    }
}
