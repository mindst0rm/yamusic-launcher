using System.Net.Http;
using System.Text;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace YaLauncher.Services;

internal sealed class YandexMusicDownloader
{
    private readonly HttpClient _http = new();

    private const string BaseUrl = "https://music-desktop-application.s3.yandex.net";

    public async Task<(string FilePath, long TotalBytes)> DownloadLatestAsync(
        string workDir,
        int parallel,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(workDir);
        var tempDir = Path.Combine(workDir, "temp");
        Directory.CreateDirectory(tempDir);

        // 1) latest.yml
        var latestYmlUrl = $"{BaseUrl}/stable/latest.yml";
        var yamlRaw = await _http.GetStringAsync(latestYmlUrl, ct);

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var map = deserializer.Deserialize<Dictionary<string, object>>(yamlRaw);
        if (!map.TryGetValue("path", out var pathObj) || pathObj is not string relPath || string.IsNullOrWhiteSpace(relPath))
            throw new InvalidDataException("latest.yml не содержит валидного поля 'path'.");

        var fullUrl = new Uri($"{BaseUrl}/stable/{relPath}");

        // 2) качаем EXE многопоточно
        var targetFile = Path.Combine(tempDir, "stable.exe");
        long totalBytes = 0;

        // обёртка прогресса — подхватим total позже
        var relay = new Progress<DownloadProgress>(p =>
        {
            totalBytes = p.TotalBytes;
            progress?.Report(p);
        });

        await MultiPartDownloader.DownloadAsync(_http, fullUrl, targetFile,
            parallel: Math.Max(1, parallel),
            progress: relay,
            ct: ct);

        return (targetFile, totalBytes);
    }
    
    // Упрощённая перегрузка для старых вызовов: (workDir, ct) -> string
    public async Task<string> DownloadLatestAsync(string workDir, CancellationToken ct = default)
    {
        var (filePath, _) = await DownloadLatestAsync(workDir, parallel: 4, progress: null, ct);
        return filePath;
    }

    // Ещё одна обёртка: (workDir, parallel, ct) -> string (если вдруг пригодится)
    public async Task<string> DownloadLatestAsync(string workDir, int parallel, CancellationToken ct = default)
    {
        var (filePath, _) = await DownloadLatestAsync(workDir, parallel, progress: null, ct);
        return filePath;
    }
}