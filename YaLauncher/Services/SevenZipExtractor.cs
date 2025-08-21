using System.Diagnostics;
using System.Text.RegularExpressions;

namespace YaLauncher.Services;

internal static class SevenZipExtractor
{
    private static readonly Regex PercentRx = new(@"(\d{1,3})%", RegexOptions.Compiled);

    public static async Task ExtractAsync(
        string sevenZipExe,
        string archivePath,
        string outputDir,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputDir);

        // -bsp1 — прогресс в stdout, -bso1 — normal output в stdout, -bse1 — ошибки в stdout (проще парсить)
        var psi = new ProcessStartInfo(sevenZipExe)
        {
            Arguments = $"x \"{archivePath}\" -o\"{outputDir}\" -y -bsp1 -bso1 -bse1 -bd",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };

        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        p.Exited += (_, __) => tcs.TrySetResult(p.ExitCode);

        if (!p.Start())
            throw new InvalidOperationException("Не удалось запустить 7za.exe");

        // читаем stdout, выдёргиваем проценты
        _ = Task.Run(async () =>
        {
            string? line;
            while ((line = await p.StandardOutput.ReadLineAsync()) is not null)
            {
                var m = PercentRx.Match(line);
                if (m.Success && int.TryParse(m.Groups[1].Value, out var prc))
                {
                    progress?.Report(Math.Clamp(prc, 0, 100));
                }
            }
        }, ct);

        ct.Register(() =>
        {
            try { if (!p.HasExited) p.Kill(true); } catch { /* ignore */ }
        });

        var code = await tcs.Task;
        if (code != 0)
            throw new InvalidOperationException($"7za завершился с кодом {code}.");
        progress?.Report(100);
    }
    
    public static Task ExtractAsync(
        string sevenZipExe,
        string archivePath,
        string outputDir,
        CancellationToken ct = default)
        => ExtractAsync(sevenZipExe, archivePath, outputDir, progress: null, ct);
}
