using System.Net.Http.Headers;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace YaLauncher.Services;

internal static class MultiPartDownloader
{
    private const int DefaultChunkSize = 4 * 1024 * 1024; // 4 MB

    public static async Task DownloadAsync(
        HttpClient http,
        Uri url,
        string targetFile,
        int parallel = 4,
        int chunkSize = DefaultChunkSize,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);

        using var head = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get, url),
                                              HttpCompletionOption.ResponseHeadersRead, ct);
        head.EnsureSuccessStatusCode();

        var total = head.Content.Headers.ContentLength ?? throw new InvalidOperationException("Не удалось определить размер файла.");
        var acceptRanges = head.Headers.AcceptRanges.Contains("bytes");

        if (!acceptRanges || parallel <= 1)
        {
            // Фоллбек на одиночную закачку с прогрессом
            await SingleStreamAsync(http, url, targetFile, total, progress, ct);
            return;
        }

        // Разбиваем на чанки
        var ranges = BuildRanges(total, chunkSize);
        using var fs = new FileStream(targetFile, FileMode.Create, FileAccess.Write, FileShare.Read,
                                      1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
        fs.SetLength(total);

        var sw = Stopwatch.StartNew();
        long received = 0;
        long lastBytes = 0;
        var lastTick = sw.Elapsed;

        var throttler = new SemaphoreSlim(parallel);
        var tasks = new List<Task>(ranges.Count);

        foreach (var (from, to) in ranges)
        {
            await throttler.WaitAsync(ct);
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.Range = new RangeHeaderValue(from, to);
                    using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                    resp.EnsureSuccessStatusCode();

                    await using var rs = await resp.Content.ReadAsStreamAsync(ct);
                    var buffer = new byte[1 << 20]; // 1 MB
                    long writePos = from;

                    while (true)
                    {
                        int n = await rs.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                        if (n <= 0) break;

                        // write chunk into file
                        lock (fs) // один fs, несколько писателей — синхронизируемся
                        {
                            fs.Position = writePos;
                            fs.Write(buffer, 0, n);
                        }
                        writePos += n;

                        // progress
                        var cur = Interlocked.Add(ref received, n);
                        if (progress != null)
                        {
                            var now = sw.Elapsed;
                            if ((now - lastTick).TotalMilliseconds >= 250)
                            {
                                var deltaBytes = cur - Interlocked.Exchange(ref lastBytes, cur);
                                var dt = (now - lastTick).TotalSeconds;
                                if (dt <= 0) dt = 0.001;
                                var speed = deltaBytes / dt;
                                TimeSpan? eta = speed > 0 ? TimeSpan.FromSeconds((total - cur) / speed) : null;
                                lastTick = now;

                                progress.Report(new DownloadProgress(total, cur, speed, eta));
                            }
                        }
                    }
                }
                finally
                {
                    throttler.Release();
                }
            }, ct));
        }

        await Task.WhenAll(tasks);

        // финальный прогресс тик
        progress?.Report(new DownloadProgress(total, total, 0, TimeSpan.Zero));
    }

    private static async Task SingleStreamAsync(
        HttpClient http, Uri url, string targetFile, long total,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var rs = await resp.Content.ReadAsStreamAsync(ct);
        await using var fs = new FileStream(targetFile, FileMode.Create, FileAccess.Write, FileShare.Read,
                                            1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);

        var sw = Stopwatch.StartNew();
        long received = 0;
        long lastBytes = 0;
        var lastTick = sw.Elapsed;
        var buf = new byte[1 << 20];

        while (true)
        {
            int n = await rs.ReadAsync(buf.AsMemory(0, buf.Length), ct);
            if (n <= 0) break;
            await fs.WriteAsync(buf.AsMemory(0, n), ct);
            received += n;

            var now = sw.Elapsed;
            if ((now - lastTick).TotalMilliseconds >= 250)
            {
                var deltaBytes = received - lastBytes;
                var dt = (now - lastTick).TotalSeconds;
                if (dt <= 0) dt = 0.001;
                var speed = deltaBytes / dt;
                TimeSpan? eta = speed > 0 ? TimeSpan.FromSeconds((total - received) / speed) : null;

                lastBytes = received;
                lastTick = now;

                progress?.Report(new DownloadProgress(total, received, speed, eta));
            }
        }
        progress?.Report(new DownloadProgress(total, total, 0, TimeSpan.Zero));
    }

    private static List<(long From, long To)> BuildRanges(long total, int chunkSize)
    {
        var list = new List<(long, long)>(checked((int)(total / chunkSize)) + 1);
        for (long pos = 0; pos < total; pos += chunkSize)
        {
            var to = Math.Min(total - 1, pos + chunkSize - 1);
            list.Add((pos, to));
        }
        return list;
    }
}
