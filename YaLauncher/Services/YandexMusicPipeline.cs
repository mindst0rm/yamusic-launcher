namespace YaLauncher.Services;

internal interface IYandexMusicDownloader
{
    Task<(string FilePath, long TotalBytes)> DownloadLatestAsync(
        string workDir,
        int parallel,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct = default);
}

internal interface IArchiveExtractor
{
    Task ExtractAsync(
        string sevenZipExe,
        string archivePath,
        string outputDir,
        IProgress<double>? progress = null,
        CancellationToken ct = default);
}

internal interface IExecutableLocator
{
    string FindExe(string root);
}

internal sealed class SevenZipArchiveExtractor : IArchiveExtractor
{
    public Task ExtractAsync(
        string sevenZipExe,
        string archivePath,
        string outputDir,
        IProgress<double>? progress = null,
        CancellationToken ct = default) =>
        SevenZipExtractor.ExtractAsync(sevenZipExe, archivePath, outputDir, progress, ct);
}

internal sealed class ExecutableFinderService : IExecutableLocator
{
    public string FindExe(string root) => ExecutableFinder.FindExe(root);
}

internal sealed class YandexMusicPipeline
{
    private readonly IYandexMusicDownloader _downloader;
    private readonly IArchiveExtractor _extractor;
    private readonly IExecutableLocator _executableLocator;

    public string WorkDir { get; }
    public string SevenZipPath { get; }

    public YandexMusicPipeline(
        string workDir,
        string sevenZipPath,
        IYandexMusicDownloader? downloader = null,
        IArchiveExtractor? extractor = null,
        IExecutableLocator? executableLocator = null)
    {
        WorkDir = workDir;
        SevenZipPath = sevenZipPath;
        _downloader = downloader ?? new YandexMusicDownloader();
        _extractor = extractor ?? new SevenZipArchiveExtractor();
        _executableLocator = executableLocator ?? new ExecutableFinderService();
    }

    public async Task<string> DownloadExtractAndLocateAsync(
        int parallel = 6,
        IProgress<DownloadProgress>? downloadProgress = null,
        IProgress<double>? extractProgress = null,
        CancellationToken ct = default)
    {
        var (archivePath, _) = await _downloader.DownloadLatestAsync(
            WorkDir,
            parallel,
            downloadProgress,
            ct);

        var unpackDir = Path.Combine(WorkDir, "unpacked");
        await _extractor.ExtractAsync(SevenZipPath, archivePath, unpackDir, extractProgress, ct);

        return _executableLocator.FindExe(unpackDir);
    }
}
