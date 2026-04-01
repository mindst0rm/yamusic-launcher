using YaLauncher.Services;

namespace YaLauncher.Tests;

public sealed class YandexMusicPipelineTests
{
    [Fact]
    public async Task DownloadExtractAndLocateAsync_UsesInjectedDependenciesInSequence()
    {
        using var temp = new TemporaryDirectory();
        var calls = new List<string>();
        var archivePath = System.IO.Path.Combine(temp.Path, "temp", "stable.exe");

        var downloader = new FakeDownloader(() =>
        {
            calls.Add("download");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(archivePath)!);
            File.WriteAllText(archivePath, "archive");
            return archivePath;
        });

        var extractor = new FakeExtractor((sevenZipExe, inputArchive, outputDir) =>
        {
            calls.Add($"extract:{System.IO.Path.GetFileName(inputArchive)}");
            Directory.CreateDirectory(outputDir);
        });

        var locator = new FakeLocator(root =>
        {
            calls.Add($"locate:{System.IO.Path.GetFileName(root)}");
            return System.IO.Path.Combine(root, "Yandex Music.exe");
        });

        var pipeline = new YandexMusicPipeline(
            temp.Path,
            "7za.exe",
            downloader,
            extractor,
            locator);

        var result = await pipeline.DownloadExtractAndLocateAsync();

        Assert.Equal(System.IO.Path.Combine(temp.Path, "unpacked", "Yandex Music.exe"), result);
        Assert.Equal(["download", "extract:stable.exe", "locate:unpacked"], calls);
    }

    private sealed class FakeDownloader : IYandexMusicDownloader
    {
        private readonly Func<string> _archiveFactory;

        public FakeDownloader(Func<string> archiveFactory)
        {
            _archiveFactory = archiveFactory;
        }

        public Task<(string FilePath, long TotalBytes)> DownloadLatestAsync(string workDir, int parallel, IProgress<DownloadProgress>? progress, CancellationToken ct = default) =>
            Task.FromResult((_archiveFactory(), 0L));
    }

    private sealed class FakeExtractor : IArchiveExtractor
    {
        private readonly Action<string, string, string> _callback;

        public FakeExtractor(Action<string, string, string> callback)
        {
            _callback = callback;
        }

        public Task ExtractAsync(string sevenZipExe, string archivePath, string outputDir, IProgress<double>? progress = null, CancellationToken ct = default)
        {
            _callback(sevenZipExe, archivePath, outputDir);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLocator : IExecutableLocator
    {
        private readonly Func<string, string> _callback;

        public FakeLocator(Func<string, string> callback)
        {
            _callback = callback;
        }

        public string FindExe(string root) => _callback(root);
    }
}
