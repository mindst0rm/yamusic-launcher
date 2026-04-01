using System.Net;
using System.Net.Http;
using YaLauncher.Application;
using YaLauncher.Services;

namespace YaLauncher.Tests;

public sealed class LauncherSelfUpdaterTests
{
    [Fact]
    public async Task TrySelfUpdateAsync_ReturnsUpToDate_WhenLatestReleaseMatchesCurrentVersion()
    {
        using var apiClient = new HttpClient(new StubHttpMessageHandler(_ => HttpResponses.Json("""
        {
          "tag_name": "v1.1.6",
          "html_url": "https://github.com/mindst0rm/yamusic-launcher/releases/tag/v1.1.6",
          "assets": [
            {
              "name": "YaMusicLauncher-Setup-1.1.6.exe",
              "browser_download_url": "https://example.test/YaMusicLauncher-Setup-1.1.6.exe"
            }
          ]
        }
        """)));

        using var downloadClient = new HttpClient(new StubHttpMessageHandler(_ => throw new InvalidOperationException("Download should not happen.")));
        var processStarter = new RecordingProcessStarter();
        var updater = new LauncherSelfUpdater(apiClient, downloadClient, processStarter);

        var result = await updater.TrySelfUpdateAsync("1.1.6");

        Assert.Equal(LauncherSelfUpdateStatus.UpToDate, result.Status);
        Assert.False(result.UpdateStarted);
        Assert.Null(processStarter.StartedPath);
    }

    [Fact]
    public async Task TrySelfUpdateAsync_DownloadsInstallerAndStartsProcess_WhenNewerReleaseExists()
    {
        using var temp = new TemporaryDirectory();
        var originalTemp = Environment.GetEnvironmentVariable("TEMP");
        Environment.SetEnvironmentVariable("TEMP", temp.Path);

        try
        {
            using var apiClient = new HttpClient(new StubHttpMessageHandler(_ => HttpResponses.Json("""
            {
              "tag_name": "v1.1.7",
              "html_url": "https://github.com/mindst0rm/yamusic-launcher/releases/tag/v1.1.7",
              "assets": [
                {
                  "name": "YaMusicLauncher-Setup-1.1.7.exe",
                  "browser_download_url": "https://example.test/YaMusicLauncher-Setup-1.1.7.exe"
                }
              ]
            }
            """)));

            using var downloadClient = new HttpClient(new StubHttpMessageHandler(request =>
            {
                Assert.Equal("https://example.test/YaMusicLauncher-Setup-1.1.7.exe", request.RequestUri!.ToString());
                return HttpResponses.Bytes([1, 2, 3, 4], HttpStatusCode.OK);
            }));

            var processStarter = new RecordingProcessStarter();
            var updater = new LauncherSelfUpdater(apiClient, downloadClient, processStarter);

            var result = await updater.TrySelfUpdateAsync("1.1.6");

            Assert.Equal(LauncherSelfUpdateStatus.UpdateStarted, result.Status);
            Assert.NotNull(result.InstallerPath);
            Assert.True(File.Exists(result.InstallerPath!));
            Assert.Equal(result.InstallerPath, processStarter.StartedPath);
            Assert.Contains("/VERYSILENT", processStarter.StartedArguments, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEMP", originalTemp);
        }
    }

    [Fact]
    public async Task TrySelfUpdateAsync_ReturnsNoInstallerAsset_WhenReleaseDoesNotContainSetup()
    {
        using var apiClient = new HttpClient(new StubHttpMessageHandler(_ => HttpResponses.Json("""
        {
          "tag_name": "v1.1.7",
          "html_url": "https://github.com/mindst0rm/yamusic-launcher/releases/tag/v1.1.7",
          "assets": [
            {
              "name": "notes.txt",
              "browser_download_url": "https://example.test/notes.txt"
            }
          ]
        }
        """)));

        using var downloadClient = new HttpClient(new StubHttpMessageHandler(_ => throw new InvalidOperationException("Download should not happen.")));
        var processStarter = new RecordingProcessStarter();
        var updater = new LauncherSelfUpdater(apiClient, downloadClient, processStarter);

        var result = await updater.TrySelfUpdateAsync("1.1.6");

        Assert.Equal(LauncherSelfUpdateStatus.NoInstallerAsset, result.Status);
        Assert.False(result.UpdateStarted);
        Assert.Null(processStarter.StartedPath);
    }

    private sealed class RecordingProcessStarter : IExternalProcessStarter
    {
        public string? StartedPath { get; private set; }
        public string StartedArguments { get; private set; } = string.Empty;

        public void Start(string filePath, string arguments)
        {
            StartedPath = filePath;
            StartedArguments = arguments;
        }
    }
}
