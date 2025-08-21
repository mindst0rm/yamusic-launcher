namespace YaLauncher.Services;

internal sealed class YandexMusicPipeline
{
    private readonly YandexMusicDownloader _downloader = new();

    public string WorkDir { get; }
    public string SevenZipPath { get; }

    public YandexMusicPipeline(string workDir, string sevenZipPath)
    {
        WorkDir = workDir;
        SevenZipPath = sevenZipPath;
    }

    public async Task<string> DownloadExtractAndLocateAsync(
        int parallel = 6,
        IProgress<DownloadProgress>? downloadProgress = null,
        IProgress<double>? extractProgress = null,
        CancellationToken ct = default)
    {
        // 1) скачать (многопоточно)
        var (archivePath, _) = await _downloader.DownloadLatestAsync(
            WorkDir, parallel, downloadProgress, ct);

        // 2) распаковать 7zip (с прогрессом)
        var unpackDir = Path.Combine(WorkDir, "unpacked");
        await SevenZipExtractor.ExtractAsync(SevenZipPath, archivePath, unpackDir, extractProgress, ct);

        // 3) найти .exe (оставляю твой ExecutableFinder; если его нет — используй свой локатор)
        return ExecutableFinder.FindExe(unpackDir);
    }
}