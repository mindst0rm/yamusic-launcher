using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using YaLauncher.Application;

namespace YaLauncher.Services;

internal sealed class LauncherSelfUpdater : ILauncherSelfUpdateService
{
    internal const string DefaultGitHubOwner = "mindst0rm";
    internal const string DefaultGitHubRepo = "yamusic-launcher";
    internal const string SetupPrefix = "YaMusicLauncher-Setup-";

    private readonly HttpClient _apiClient;
    private readonly HttpClient _downloadClient;
    private readonly IExternalProcessStarter _processStarter;

    public LauncherSelfUpdater(
        HttpClient? apiClient = null,
        HttpClient? downloadClient = null,
        IExternalProcessStarter? processStarter = null)
    {
        _apiClient = apiClient ?? CreateHttpClient();
        _downloadClient = downloadClient ?? CreateHttpClient();
        _processStarter = processStarter ?? new ShellExternalProcessStarter();
    }

    public async Task<LauncherSelfUpdateResult> TrySelfUpdateAsync(
        string currentVersion,
        Action<string, string>? log = null,
        CancellationToken ct = default)
    {
        try
        {
            log?.Invoke("Проверяем последний релиз лаунчера на GitHub...", "cyan");
            using var releaseCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            releaseCts.CancelAfter(TimeSpan.FromSeconds(20));

            var release = await GetLatestReleaseAsync(releaseCts.Token);
            if (release is null)
            {
                return new LauncherSelfUpdateResult(
                    LauncherSelfUpdateStatus.Failed,
                    currentVersion,
                    null,
                    null,
                    null,
                    "Не удалось получить информацию о последнем релизе лаунчера.");
            }

            var latestVersion = NormalizeVersion(release.TagName);
            if (!IsRemoteVersionNewer(currentVersion, latestVersion))
            {
                log?.Invoke($"Лаунчер уже актуален: {currentVersion}", "green");
                return new LauncherSelfUpdateResult(
                    LauncherSelfUpdateStatus.UpToDate,
                    currentVersion,
                    latestVersion,
                    release.HtmlUrl,
                    null,
                    null);
            }

            var asset = release.Assets
                .FirstOrDefault(x => x.Name.StartsWith(SetupPrefix, StringComparison.OrdinalIgnoreCase) &&
                                     x.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

            if (asset is null || string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
            {
                return new LauncherSelfUpdateResult(
                    LauncherSelfUpdateStatus.NoInstallerAsset,
                    currentVersion,
                    latestVersion,
                    release.HtmlUrl,
                    null,
                    "В GitHub Release не найден Setup-файл лаунчера.");
            }

            log?.Invoke($"Найдена новая версия лаунчера: {currentVersion} -> {latestVersion}", "green");
            log?.Invoke($"Скачиваем установщик {asset.Name}...", "cyan");

            using var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            downloadCts.CancelAfter(TimeSpan.FromMinutes(5));
            var installerPath = await DownloadInstallerAsync(asset, latestVersion, downloadCts.Token);

            log?.Invoke($"Установщик скачан: {installerPath}", "green");
            log?.Invoke("Запускаем установщик обновления...", "cyan");
            _processStarter.Start(
                installerPath,
                "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /FORCECLOSEAPPLICATIONS");

            return new LauncherSelfUpdateResult(
                LauncherSelfUpdateStatus.UpdateStarted,
                currentVersion,
                latestVersion,
                release.HtmlUrl,
                installerPath,
                $"Запущен установщик новой версии лаунчера ({latestVersion}).");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new LauncherSelfUpdateResult(
                LauncherSelfUpdateStatus.Failed,
                currentVersion,
                null,
                null,
                null,
                "Проверка или скачивание обновления лаунчера превысили лимит ожидания.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new LauncherSelfUpdateResult(
                LauncherSelfUpdateStatus.Failed,
                currentVersion,
                null,
                null,
                null,
                "Обновление лаунчера отменено пользователем.");
        }
        catch (Exception ex)
        {
            return new LauncherSelfUpdateResult(
                LauncherSelfUpdateStatus.Failed,
                currentVersion,
                null,
                null,
                null,
                $"Не удалось обновить лаунчер: {ex.Message}");
        }
    }

    private async Task<LauncherReleaseInfo?> GetLatestReleaseAsync(CancellationToken ct)
    {
        var url = $"https://api.github.com/repos/{DefaultGitHubOwner}/{DefaultGitHubRepo}/releases/latest";
        using var response = await _apiClient.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = document.RootElement;

        if (!root.TryGetProperty("tag_name", out var tagProp) || string.IsNullOrWhiteSpace(tagProp.GetString()))
            return null;

        var assets = new List<LauncherReleaseAssetInfo>();
        if (root.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in assetsProp.EnumerateArray())
            {
                var name = item.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                var downloadUrl = item.TryGetProperty("browser_download_url", out var downloadProp) ? downloadProp.GetString() : null;
                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(downloadUrl))
                    assets.Add(new LauncherReleaseAssetInfo(name!, downloadUrl!));
            }
        }

        var htmlUrl = root.TryGetProperty("html_url", out var htmlProp) ? htmlProp.GetString() : null;
        return new LauncherReleaseInfo(tagProp.GetString()!, htmlUrl, assets);
    }

    private async Task<string> DownloadInstallerAsync(LauncherReleaseAssetInfo asset, string latestVersion, CancellationToken ct)
    {
        var targetDir = Path.Combine(Path.GetTempPath(), "YaMusicLauncher", "self-update", latestVersion);
        Directory.CreateDirectory(targetDir);

        foreach (var staleDir in Directory.EnumerateDirectories(Path.Combine(Path.GetTempPath(), "YaMusicLauncher", "self-update")))
        {
            if (!string.Equals(Path.GetFileName(staleDir), latestVersion, StringComparison.OrdinalIgnoreCase))
                TryDeleteDirectory(staleDir);
        }

        var destination = Path.Combine(targetDir, asset.Name);
        using var response = await _downloadClient.GetAsync(asset.BrowserDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var file = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        await source.CopyToAsync(file, ct);
        return destination;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Ignore temp cleanup failures.
        }
    }

    private static bool IsRemoteVersionNewer(string currentVersion, string latestVersion)
    {
        if (TryParseVersion(currentVersion, out var current) && TryParseVersion(latestVersion, out var latest))
            return latest > current;

        return !string.Equals(
            NormalizeVersion(currentVersion),
            NormalizeVersion(latestVersion),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseVersion(string value, out Version version)
    {
        var normalized = NormalizeVersion(value);
        return Version.TryParse(normalized, out version!);
    }

    internal static string NormalizeVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "0.0.0";

        var trimmed = value.Trim();
        if (trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[1..];

        var numeric = new string(trimmed
            .TakeWhile(ch => char.IsDigit(ch) || ch == '.')
            .ToArray());

        return string.IsNullOrWhiteSpace(numeric) ? trimmed : numeric;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("YaMusicLauncher", NormalizeVersion(AppVersionProvider.DisplayVersion)));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }
}

internal interface IExternalProcessStarter
{
    void Start(string filePath, string arguments);
}

internal sealed class ShellExternalProcessStarter : IExternalProcessStarter
{
    public void Start(string filePath, string arguments)
    {
        var startInfo = new ProcessStartInfo(filePath)
        {
            Arguments = arguments,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(filePath) ?? Directory.GetCurrentDirectory()
        };

        Process.Start(startInfo);
    }
}

internal sealed record LauncherReleaseInfo(
    string TagName,
    string? HtmlUrl,
    IReadOnlyList<LauncherReleaseAssetInfo> Assets);

internal sealed record LauncherReleaseAssetInfo(
    string Name,
    string BrowserDownloadUrl);
