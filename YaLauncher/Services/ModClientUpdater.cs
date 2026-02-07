using System.Text;
using System.Text.Json;

namespace YaLauncher.Services;

internal sealed class ModClientUpdater
{
    private const long MaxLogSizeBytes = 5L * 1024L * 1024L;
    private readonly HttpClient _http;

    public ModClientUpdater(HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("YaMusicLauncher");
    }

    public async Task<string?> GetLatestVersionAsync(string owner, string repo, CancellationToken ct = default)
    {
        var tag = await TryGetLatestTagViaRedirectAsync(owner, repo, ct);
        if (!string.IsNullOrWhiteSpace(tag))
            return tag;

        var rel = await GetLatestReleaseInfoAsync(owner, repo, ct);
        return rel?.TagName;
    }

    public async Task<IReadOnlyList<ModReleaseInfo>> GetRecentReleasesAsync(
        string owner,
        string repo,
        int limit = 8,
        CancellationToken ct = default)
    {
        var count = Math.Clamp(limit, 1, 20);
        var api = $"https://api.github.com/repos/{owner}/{repo}/releases?per_page={count}";

        using var req = new HttpRequestMessage(HttpMethod.Get, api);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
            return [];

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return [];

        var releases = new List<ModReleaseInfo>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var tag = item.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag))
                continue;

            var name = item.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
            var body = item.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() : null;
            var prerelease = item.TryGetProperty("prerelease", out var preProp) && preProp.GetBoolean();
            var publishedAt = ParseDate(item, "published_at");

            releases.Add(new ModReleaseInfo(tag!, name, body, prerelease, publishedAt));
        }

        return releases;
    }

    public async Task<ModInstallResult> InstallLatestAsync(
        string installDir,
        string owner,
        string repo,
        int backupAutoCleanupLimitMb = 300,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        var paths = GetPaths(installDir);
        EnsureDirs(paths);

        var installed = GetInstalledVersion(paths.VersionFile);
        var (downloadUrl, tag) = await ResolveDownloadUrlAsync(owner, repo, ct);
        Log(paths.LogFile, $"Resolved latest tag={tag}, url={downloadUrl}", "INFO");

        if (!string.IsNullOrWhiteSpace(installed) &&
            !string.IsNullOrWhiteSpace(tag) &&
            string.Equals(installed, tag, StringComparison.OrdinalIgnoreCase))
        {
            Log(paths.LogFile, $"Skip update. Local version {installed} is latest.", "INFO");
            return new ModInstallResult(false, installed, tag);
        }

        var tempFile = Path.Combine(Path.GetTempPath(), $"app_{Guid.NewGuid():N}.asar");
        try
        {
            await DownloadFileAsync(downloadUrl, tempFile, progress, ct);
            BackupExistingAppAndVersion(paths);
            EnforceBackupLimit(paths, backupAutoCleanupLimitMb);
            File.Copy(tempFile, paths.AppFile, overwrite: true);
            if (!string.IsNullOrWhiteSpace(tag))
                SetInstalledVersion(paths.VersionFile, tag!);

            Log(paths.LogFile, $"Installed app.asar. Version={tag ?? "unknown"}", "INFO");
            return new ModInstallResult(true, installed, tag);
        }
        finally
        {
            TryDelete(tempFile);
        }
    }

    public IReadOnlyList<ModBackupEntry> ListBackups(string installDir, int backupAutoCleanupLimitMb = 300)
    {
        var paths = GetPaths(installDir);
        EnsureDirs(paths);
        EnforceBackupLimit(paths, backupAutoCleanupLimitMb);

        if (!Directory.Exists(paths.BackupDir))
            return [];

        return Directory
            .EnumerateFiles(paths.BackupDir, "app_*.asar", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => new ModBackupEntry(f.FullName, f.Name, f.LastWriteTime))
            .ToArray();
    }

    public void RestoreBackup(string installDir, string backupAsarPath, int backupAutoCleanupLimitMb = 300)
    {
        var paths = GetPaths(installDir);
        EnsureDirs(paths);

        if (!File.Exists(backupAsarPath))
            throw new FileNotFoundException("Backup file not found.", backupAsarPath);

        BackupExistingAppAndVersion(paths);
        EnforceBackupLimit(paths, backupAutoCleanupLimitMb);
        File.Copy(backupAsarPath, paths.AppFile, overwrite: true);
        RestoreVersionFromBackup(paths, backupAsarPath);
        Log(paths.LogFile, $"Restored app.asar from backup: {backupAsarPath}", "INFO");
    }

    public bool DeleteBackup(string installDir, string backupAsarPath)
    {
        var paths = GetPaths(installDir);
        EnsureDirs(paths);
        if (string.IsNullOrWhiteSpace(backupAsarPath) || !File.Exists(backupAsarPath))
            return false;

        var removed = TryDeleteBackupWithVersion(paths, backupAsarPath);
        if (removed)
            Log(paths.LogFile, $"Deleted backup: {backupAsarPath}", "INFO");
        return removed;
    }

    public int DeleteAllBackups(string installDir)
    {
        var paths = GetPaths(installDir);
        EnsureDirs(paths);

        var removed = 0;
        var files = Directory.EnumerateFiles(paths.BackupDir, "app_*.*", SearchOption.TopDirectoryOnly).ToArray();
        foreach (var file in files)
        {
            if (!IsBackupFileName(Path.GetFileName(file)))
                continue;

            try
            {
                File.Delete(file);
                removed++;
            }
            catch
            {
                // ignore per-file failures
            }
        }

        Log(paths.LogFile, $"Deleted all backups. Removed files={removed}", "INFO");
        return removed;
    }

    public long GetBackupDirectorySizeBytes(string installDir)
    {
        var paths = GetPaths(installDir);
        if (!Directory.Exists(paths.BackupDir))
            return 0;

        return Directory.EnumerateFiles(paths.BackupDir, "*", SearchOption.TopDirectoryOnly)
            .Select(path =>
            {
                try { return new FileInfo(path).Length; }
                catch { return 0L; }
            })
            .Sum();
    }

    public static string GetResourceDir(string installDir) => Path.Combine(installDir, "resources");

    private async Task DownloadFileAsync(
        string url,
        string destination,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength ?? 0;
        await using var input = await resp.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 1024 * 1024, useAsync: true);

        var buffer = new byte[1024 * 1024];
        long readTotal = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long lastBytes = 0;
        var lastTick = sw.Elapsed;

        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            if (read <= 0)
                break;

            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            readTotal += read;

            if (progress == null)
                continue;

            var now = sw.Elapsed;
            if ((now - lastTick).TotalMilliseconds < 250)
                continue;

            var deltaBytes = readTotal - lastBytes;
            var dt = Math.Max(0.001, (now - lastTick).TotalSeconds);
            var speed = deltaBytes / dt;
            TimeSpan? eta = speed > 0 && total > 0
                ? TimeSpan.FromSeconds(Math.Max(0, (total - readTotal) / speed))
                : null;

            lastBytes = readTotal;
            lastTick = now;
            progress.Report(new DownloadProgress(total, readTotal, speed, eta));
        }

        progress?.Report(new DownloadProgress(total, readTotal, 0, TimeSpan.Zero));
    }

    private async Task<(string Url, string? Tag)> ResolveDownloadUrlAsync(string owner, string repo, CancellationToken ct)
    {
        var tag = await TryGetLatestTagViaRedirectAsync(owner, repo, ct);
        if (!string.IsNullOrWhiteSpace(tag))
        {
            var url = $"https://github.com/{owner}/{repo}/releases/download/{tag}/app.asar";
            return (url, tag);
        }

        var release = await GetLatestReleaseInfoAsync(owner, repo, ct)
                      ?? throw new InvalidOperationException("Unable to resolve latest release from GitHub.");

        var asset = release.Assets
            .FirstOrDefault(a => string.Equals(a.Name, "app.asar", StringComparison.OrdinalIgnoreCase))
            ?? release.Assets.FirstOrDefault(a =>
                !string.IsNullOrWhiteSpace(a.Name) &&
                a.Name.EndsWith(".asar", StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(asset?.BrowserDownloadUrl))
            return (asset.BrowserDownloadUrl!, release.TagName);

        if (!string.IsNullOrWhiteSpace(release.TagName))
            return ($"https://github.com/{owner}/{repo}/releases/download/{release.TagName}/app.asar", release.TagName);

        throw new InvalidOperationException("Unable to resolve app.asar download URL.");
    }

    private async Task<string?> TryGetLatestTagViaRedirectAsync(string owner, string repo, CancellationToken ct)
    {
        var latestUrl = $"https://github.com/{owner}/{repo}/releases/latest";
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var redirectClient = new HttpClient(handler);
        redirectClient.DefaultRequestHeaders.UserAgent.ParseAdd("YaMusicLauncher");

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, latestUrl);
            using var resp = await redirectClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)resp.StatusCode is < 300 or >= 400)
                return null;

            var location = resp.Headers.Location;
            if (location == null)
                return null;

            var path = location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString;
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var tagIdx = Array.FindIndex(parts, p => string.Equals(p, "tag", StringComparison.OrdinalIgnoreCase));
            return tagIdx >= 0 && tagIdx < parts.Length - 1 ? parts[tagIdx + 1] : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<GitHubReleaseResponse?> GetLatestReleaseInfoAsync(string owner, string repo, CancellationToken ct)
    {
        var api = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";
        using var req = new HttpRequestMessage(HttpMethod.Get, api);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
            return null;

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var root = doc.RootElement;
        var tag = root.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() : null;

        var assets = new List<GitHubAsset>();
        if (root.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in assetsProp.EnumerateArray())
            {
                var name = item.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                var url = item.TryGetProperty("browser_download_url", out var urlProp) ? urlProp.GetString() : null;
                assets.Add(new GitHubAsset(name, url));
            }
        }

        return new GitHubReleaseResponse(tag, assets);
    }

    private static DateTimeOffset? ParseDate(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var prop))
            return null;
        if (prop.ValueKind != JsonValueKind.String)
            return null;

        var str = prop.GetString();
        return DateTimeOffset.TryParse(str, out var dt) ? dt : null;
    }

    private static Paths GetPaths(string installDir)
    {
        var resources = GetResourceDir(installDir);
        return new Paths(
            resources,
            Path.Combine(resources, "app.asar"),
            Path.Combine(resources, "backups_app"),
            Path.Combine(resources, "app.version"),
            Path.Combine(resources, "logs"),
            Path.Combine(resources, "logs", "ym_mod_manager.log")
        );
    }

    private static void EnsureDirs(Paths paths)
    {
        Directory.CreateDirectory(paths.ResourcesDir);
        Directory.CreateDirectory(paths.BackupDir);
        Directory.CreateDirectory(paths.LogDir);
    }

    private static void BackupExistingAppAndVersion(Paths paths)
    {
        if (!File.Exists(paths.AppFile) && !File.Exists(paths.VersionFile))
            return;

        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        if (File.Exists(paths.AppFile))
        {
            var asarBackup = Path.Combine(paths.BackupDir, $"app_{stamp}.asar");
            File.Copy(paths.AppFile, asarBackup, overwrite: true);
        }

        if (File.Exists(paths.VersionFile))
        {
            var versionBackup = Path.Combine(paths.BackupDir, $"app_{stamp}.version");
            File.Copy(paths.VersionFile, versionBackup, overwrite: true);
        }
    }

    private static void EnforceBackupLimit(Paths paths, int maxSizeMb)
    {
        if (maxSizeMb <= 0 || !Directory.Exists(paths.BackupDir))
            return;

        var maxBytes = maxSizeMb * 1024L * 1024L;
        long currentBytes = 0;
        var asarFiles = Directory
            .EnumerateFiles(paths.BackupDir, "app_*.asar", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderBy(f => f.LastWriteTimeUtc)
            .ToList();

        foreach (var asar in asarFiles)
            currentBytes += asar.Length;

        var versionFiles = Directory
            .EnumerateFiles(paths.BackupDir, "app_*.version", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var v in versionFiles.Values)
            currentBytes += v.Length;

        if (currentBytes <= maxBytes)
            return;

        foreach (var asar in asarFiles)
        {
            currentBytes -= asar.Length;
            TryDelete(asar.FullName);

            var stamp = TryGetBackupStamp(asar.Name);
            if (!string.IsNullOrWhiteSpace(stamp))
            {
                var versionName = $"app_{stamp}.version";
                if (versionFiles.TryGetValue(versionName, out var version))
                {
                    currentBytes -= version.Length;
                    TryDelete(version.FullName);
                    versionFiles.Remove(versionName);
                }
            }

            if (currentBytes <= maxBytes)
                break;
        }
    }

    private static bool TryDeleteBackupWithVersion(Paths paths, string backupAsarPath)
    {
        var removed = false;
        try
        {
            File.Delete(backupAsarPath);
            removed = true;
        }
        catch
        {
            // ignore
        }

        var name = Path.GetFileName(backupAsarPath);
        var stamp = TryGetBackupStamp(name);
        if (!string.IsNullOrWhiteSpace(stamp))
        {
            var versionPath = Path.Combine(paths.BackupDir, $"app_{stamp}.version");
            TryDelete(versionPath);
        }

        return removed;
    }

    private static string? TryGetBackupStamp(string fileName)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            fileName,
            @"^app_(\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2})\.(asar|version)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static bool IsBackupFileName(string fileName) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            fileName,
            @"^app_\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}\.(asar|version)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static string? GetInstalledVersion(string versionFile)
    {
        if (!File.Exists(versionFile))
            return null;

        var value = File.ReadAllText(versionFile, Encoding.UTF8).Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static void SetInstalledVersion(string versionFile, string value)
    {
        File.WriteAllText(versionFile, value.Trim(), Encoding.UTF8);
    }

    private static void ClearInstalledVersion(string versionFile)
    {
        File.WriteAllText(versionFile, string.Empty, Encoding.UTF8);
    }

    private static void RestoreVersionFromBackup(Paths paths, string backupAsarPath)
    {
        var name = Path.GetFileName(backupAsarPath);
        var match = System.Text.RegularExpressions.Regex.Match(name, @"^app_(\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2})\.asar$");
        if (!match.Success)
        {
            ClearInstalledVersion(paths.VersionFile);
            return;
        }

        var stamp = match.Groups[1].Value;
        var versionBackup = Path.Combine(paths.BackupDir, $"app_{stamp}.version");
        if (!File.Exists(versionBackup))
        {
            ClearInstalledVersion(paths.VersionFile);
            return;
        }

        var version = File.ReadAllText(versionBackup, Encoding.UTF8).Trim();
        if (string.IsNullOrWhiteSpace(version))
            ClearInstalledVersion(paths.VersionFile);
        else
            SetInstalledVersion(paths.VersionFile, version);
    }

    private static void Log(string logFile, string message, string level)
    {
        try
        {
            RotateLogIfNeeded(logFile);
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}";
            File.AppendAllText(logFile, line + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
            // ignore logging failures
        }
    }

    private static void RotateLogIfNeeded(string logFile)
    {
        if (!File.Exists(logFile))
            return;

        var info = new FileInfo(logFile);
        if (info.Length < MaxLogSizeBytes)
            return;

        var dir = info.DirectoryName ?? Path.GetDirectoryName(logFile) ?? ".";
        var rotated = Path.Combine(dir, $"ym_mod_manager_{DateTime.Now:yyyyMMdd_HHmmss}.log");
        File.Move(logFile, rotated, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // ignore
        }
    }

    private sealed record Paths(
        string ResourcesDir,
        string AppFile,
        string BackupDir,
        string VersionFile,
        string LogDir,
        string LogFile);

    private sealed record GitHubReleaseResponse(string? TagName, IReadOnlyList<GitHubAsset> Assets);
    private sealed record GitHubAsset(string? Name, string? BrowserDownloadUrl);
}

internal sealed record ModInstallResult(bool Updated, string? InstalledVersion, string? LatestVersion);
internal sealed record ModBackupEntry(string FullPath, string FileName, DateTime CreatedAt);
internal sealed record ModReleaseInfo(
    string Tag,
    string? Name,
    string? Body,
    bool IsPreRelease,
    DateTimeOffset? PublishedAt);
