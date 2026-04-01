using System.Net;
using System.Net.Http;
using System.Text;
using YaLauncher.Services;

namespace YaLauncher.Tests;

public sealed class ModClientUpdaterTests
{
    [Fact]
    public async Task GetLatestVersionAsync_UsesRedirectTagWhenAvailable()
    {
        using var http = new HttpClient(new StubHttpMessageHandler(_ => HttpResponses.Json("{}")));
        using var redirectHttp = new HttpClient(new StubHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("https://github.com/owner/repo/releases/tag/v9.9.9");
            return response;
        }));

        var updater = new ModClientUpdater(http, redirectHttp);

        var result = await updater.GetLatestVersionAsync("owner", "repo");

        Assert.Equal("v9.9.9", result);
    }

    [Fact]
    public async Task GetRecentReleasesAsync_ParsesJsonPayload()
    {
        const string json = """
        [
          {
            "tag_name": "v2.0.0",
            "name": "Release 2",
            "body": "Line 1\nLine 2",
            "prerelease": false,
            "published_at": "2026-03-01T10:00:00Z"
          }
        ]
        """;

        using var http = new HttpClient(new StubHttpMessageHandler(_ => HttpResponses.Json(json)));
        using var redirectHttp = new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        var updater = new ModClientUpdater(http, redirectHttp);

        var releases = await updater.GetRecentReleasesAsync("owner", "repo");

        var release = Assert.Single(releases);
        Assert.Equal("v2.0.0", release.Tag);
        Assert.Equal("Release 2", release.Name);
        Assert.Equal("Line 1\nLine 2", release.Body);
        Assert.False(release.IsPreRelease);
        Assert.Equal(2026, release.PublishedAt?.Year);
    }

    [Fact]
    public async Task InstallLatestAsync_DownloadsAppAndCreatesBackupAndLog()
    {
        using var temp = new TemporaryDirectory();
        var installDir = temp.Path;
        var resourcesDir = System.IO.Path.Combine(installDir, "resources");
        Directory.CreateDirectory(resourcesDir);
        File.WriteAllText(System.IO.Path.Combine(resourcesDir, "app.asar"), "old-app", Encoding.UTF8);
        File.WriteAllText(System.IO.Path.Combine(resourcesDir, "app.version"), "v1.0.0", Encoding.UTF8);

        using var http = new HttpClient(new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri?.AbsoluteUri.Contains("/releases/download/v2.0.0/app.asar", StringComparison.OrdinalIgnoreCase) == true)
                return HttpResponses.Bytes(Encoding.UTF8.GetBytes("new-app"));

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        using var redirectHttp = new HttpClient(new StubHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("https://github.com/owner/repo/releases/tag/v2.0.0");
            return response;
        }));

        var updater = new ModClientUpdater(http, redirectHttp);

        var result = await updater.InstallLatestAsync(installDir, "owner", "repo");

        Assert.True(result.Updated);
        Assert.Equal("v1.0.0", result.InstalledVersion);
        Assert.Equal("v2.0.0", result.LatestVersion);
        Assert.Equal("new-app", File.ReadAllText(System.IO.Path.Combine(resourcesDir, "app.asar"), Encoding.UTF8));
        Assert.Equal("v2.0.0", File.ReadAllText(System.IO.Path.Combine(resourcesDir, "app.version"), Encoding.UTF8));
        Assert.Single(updater.ListBackups(installDir));
        Assert.True(File.Exists(System.IO.Path.Combine(resourcesDir, "logs", "ym_mod_manager.log")));
    }

    [Fact]
    public void BackupOperations_RestoreDeleteAndMeasureDirectory()
    {
        using var temp = new TemporaryDirectory();
        var installDir = temp.Path;
        var resourcesDir = System.IO.Path.Combine(installDir, "resources");
        var backupDir = System.IO.Path.Combine(resourcesDir, "backups_app");
        Directory.CreateDirectory(backupDir);

        File.WriteAllText(System.IO.Path.Combine(resourcesDir, "app.asar"), "current", Encoding.UTF8);
        File.WriteAllText(System.IO.Path.Combine(resourcesDir, "app.version"), "v-current", Encoding.UTF8);

        var backupAsar = System.IO.Path.Combine(backupDir, "app_2026-03-01_10-00-00.asar");
        var backupVersion = System.IO.Path.Combine(backupDir, "app_2026-03-01_10-00-00.version");
        File.WriteAllText(backupAsar, "backup", Encoding.UTF8);
        File.WriteAllText(backupVersion, "v-backup", Encoding.UTF8);

        var updater = new ModClientUpdater(new HttpClient(new StubHttpMessageHandler(_ => HttpResponses.Json("{}"))));

        Assert.True(updater.GetBackupDirectorySizeBytes(installDir) > 0);

        updater.RestoreBackup(installDir, backupAsar);
        Assert.Equal("backup", File.ReadAllText(System.IO.Path.Combine(resourcesDir, "app.asar"), Encoding.UTF8));
        Assert.Equal("v-backup", File.ReadAllText(System.IO.Path.Combine(resourcesDir, "app.version"), Encoding.UTF8));

        Assert.True(updater.DeleteBackup(installDir, backupAsar));
        Assert.Single(updater.ListBackups(installDir));

        File.WriteAllText(System.IO.Path.Combine(backupDir, "app_2026-03-02_10-00-00.asar"), "another", Encoding.UTF8);
        File.WriteAllText(System.IO.Path.Combine(backupDir, "app_2026-03-02_10-00-00.version"), "v-another", Encoding.UTF8);

        Assert.True(updater.DeleteAllBackups(installDir) > 0);
        Assert.Empty(updater.ListBackups(installDir));
    }
}



