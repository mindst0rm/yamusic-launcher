using System.Diagnostics;
using System.Runtime.Versioning;
using Spectre.Console;
using YaLauncher.Application;
using YaLauncher.Native;
using YaLauncher.Storage;
using YaLauncher.Utils;

namespace YaLauncher.Services;

internal sealed class LauncherPrerequisites : ILauncherPrerequisites
{
    private readonly LauncherPaths _paths;

    public LauncherPrerequisites(LauncherPaths paths)
    {
        _paths = paths;
    }

    public void EnsureSevenZipAvailable()
    {
        if (!File.Exists(_paths.SevenZipExePath))
            throw new FileNotFoundException($"7za.exe не найден по пути: {_paths.SevenZipExePath}");
    }

    public void ValidateInstallPathForDelete(string installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir))
            throw new ArgumentException("InstallDir is empty.", nameof(installDir));

        var full = Path.GetFullPath(installDir);
        var root = Path.GetPathRoot(full);
        var launcherDir = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\');

        if (string.Equals(full.TrimEnd('\\'), root?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Небезопасный путь установки: {full}");

        if (full.Length < 8)
            throw new InvalidOperationException($"Подозрительно короткий путь установки: {full}");

        if (full.TrimEnd('\\').StartsWith(launcherDir, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Каталог установки не должен совпадать с каталогом лаунчера или быть внутри него.");
    }
}

internal sealed class ProcessControllerAdapter : IProcessController
{
    private readonly YandexProcessManager _processManager;

    public ProcessControllerAdapter(YandexProcessManager? processManager = null)
    {
        _processManager = processManager ?? new YandexProcessManager();
    }

    public Task StopAllAsync(CancellationToken ct = default) =>
        _processManager.StopAllAsync(ct);
}

internal sealed class InstalledClientLocator : IInstalledClientLocator
{
    public string FindInstalledExeOrThrow(string installDir)
    {
        if (!Directory.Exists(installDir))
            throw new DirectoryNotFoundException($"Каталог установки не найден: {installDir}");

        return ExecutableFinder.FindExe(installDir);
    }

    public bool IsInitialSetupDone(string installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
            return false;

        try
        {
            _ = ExecutableFinder.FindExe(installDir);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public string? TryFindInstalledExe(string installDir)
    {
        try
        {
            return FindInstalledExeOrThrow(installDir);
        }
        catch
        {
            return null;
        }
    }
}

internal sealed class SpectreClientInstallationService : IClientInstallationService
{
    private readonly LauncherPaths _paths;
    private readonly IYandexMusicDownloader _downloader;
    private readonly IArchiveExtractor _extractor;
    private readonly IExecutableLocator _executableLocator;

    public SpectreClientInstallationService(
        LauncherPaths paths,
        IYandexMusicDownloader? downloader = null,
        IArchiveExtractor? extractor = null,
        IExecutableLocator? executableLocator = null)
    {
        _paths = paths;
        _downloader = downloader ?? new YandexMusicDownloader();
        _extractor = extractor ?? new SevenZipArchiveExtractor();
        _executableLocator = executableLocator ?? new ExecutableFinderService();
    }

    public async Task<string> InstallLatestClientAsync(
        string installDir,
        int parallel,
        string stagePrefix = "",
        CancellationToken ct = default)
    {
        string archivePath = string.Empty;
        string locatedExe = string.Empty;

        await AnsiConsole.Progress()
            .Columns(new ProgressColumn[]
            {
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new DownloadedColumn(),
                new TransferSpeedColumn(),
                new RemainingTimeColumn()
            })
            .StartAsync(async ctx =>
            {
                var cleanupTask = ctx.AddTask(BuildStageTaskMarkup(stagePrefix, "Подготовка каталога установки", "grey"), maxValue: 1, autoStart: true);
                var downloadTask = ctx.AddTask(BuildStageTaskMarkup(stagePrefix, "Загрузка клиента Я.Музыки", "cyan"), autoStart: false);
                var extractTask = ctx.AddTask(BuildStageTaskMarkup(stagePrefix, "Распаковка клиента", "yellow"), maxValue: 100, autoStart: false);

                if (Directory.Exists(installDir))
                    SafeDelete.DeleteDirectory(installDir);
                Directory.CreateDirectory(installDir);
                cleanupTask.Value = 1;

                downloadTask.StartTask();
                var downloadProgress = new Progress<DownloadProgress>(progress =>
                {
                    if (progress.TotalBytes > 0 && downloadTask.MaxValue == 100)
                        downloadTask.MaxValue = Math.Max(1, progress.TotalBytes);

                    downloadTask.Value = progress.ReceivedBytes;
                    if (progress.TotalBytes > 0)
                    {
                        var description =
                            $"{BuildStageTaskTitle(stagePrefix, "Загрузка клиента Я.Музыки")} ({FormatBytes(progress.ReceivedBytes)}/{FormatBytes(progress.TotalBytes)})";
                        downloadTask.Description = $"[cyan]{Markup.Escape(description)}[/]";
                    }
                });

                (archivePath, _) = await _downloader.DownloadLatestAsync(_paths.WorkDir, parallel, downloadProgress, ct);
                downloadTask.Value = downloadTask.MaxValue;

                extractTask.StartTask();
                await _extractor.ExtractAsync(
                    _paths.SevenZipExePath,
                    archivePath,
                    installDir,
                    new Progress<double>(progress => extractTask.Value = progress),
                    ct);
                extractTask.Value = extractTask.MaxValue;

                locatedExe = _executableLocator.FindExe(installDir);
            });

        return locatedExe;
    }

    private static string BuildStageTaskTitle(string stagePrefix, string title) =>
        string.IsNullOrWhiteSpace(stagePrefix) ? title : $"Шаг {stagePrefix}: {title}";

    private static string BuildStageTaskMarkup(string stagePrefix, string title, string color) =>
        $"[{color}]{Markup.Escape(BuildStageTaskTitle(stagePrefix, title))}[/]";

    private static string FormatBytes(long value)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double current = value;
        var index = 0;
        while (current >= 1024 && index < units.Length - 1)
        {
            current /= 1024;
            index++;
        }

        return $"{current:0.##} {units[index]}";
    }
}

internal sealed class SpectreModInstallationService : IModInstallationService
{
    private readonly ModClientUpdater _updater;

    public SpectreModInstallationService(ModClientUpdater? updater = null)
    {
        _updater = updater ?? new ModClientUpdater();
    }

    public async Task<ModInstallResult> InstallLatestModAsync(
        AppConfig cfg,
        string installDir,
        string stagePrefix = "",
        CancellationToken ct = default)
    {
        ModInstallResult? result = null;

        await AnsiConsole.Progress()
            .Columns(new ProgressColumn[]
            {
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new DownloadedColumn(),
                new TransferSpeedColumn(),
                new RemainingTimeColumn()
            })
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask(BuildStageTaskMarkup(stagePrefix, "Установка мод-клиента (app.asar)", "green"), autoStart: true);
                var progress = new Progress<DownloadProgress>(download =>
                {
                    if (download.TotalBytes > 0 && task.MaxValue == 100)
                        task.MaxValue = Math.Max(1, download.TotalBytes);

                    task.Value = download.ReceivedBytes;
                });

                result = await _updater.InstallLatestAsync(
                    installDir,
                    cfg.GitHubOwner,
                    cfg.GitHubRepo,
                    cfg.BackupAutoCleanupLimitMb,
                    progress,
                    ct);
                task.Value = task.MaxValue;
            });

        return result ?? throw new InvalidOperationException("Не удалось получить результат обновления мода.");
    }

    private static string BuildStageTaskTitle(string stagePrefix, string title) =>
        string.IsNullOrWhiteSpace(stagePrefix) ? title : $"Шаг {stagePrefix}: {title}";

    private static string BuildStageTaskMarkup(string stagePrefix, string title, string color) =>
        $"[{color}]{Markup.Escape(BuildStageTaskTitle(stagePrefix, title))}[/]";
}

internal sealed class FusePatchService : IPatchService
{
    public int ApplyPatchOrThrow(string exePath)
    {
        var dryRun = FuseLib.Disable(exePath, dryRun: true, limit: -1, out var dryError);
        if (dryRun < 0)
            throw new InvalidOperationException(DescribeFuseFailure(dryRun, dryError));

        var result = FuseLib.Disable(exePath, dryRun: false, limit: -1, out var applyError);
        if (result < 0)
            throw new InvalidOperationException(DescribeFuseFailure(result, applyError));

        return result;
    }

    private static string DescribeFuseFailure(int rc, string? err)
    {
        var reason = rc switch
        {
            FuseLib.E_ARGS => "Неверные аргументы/путь (E_ARGS)",
            FuseLib.E_IO => "Ошибка ввода-вывода (E_IO)",
            FuseLib.E_PE => "Ошибка парсинга PE (E_PE)",
            FuseLib.E_FAIL => "Неизвестная ошибка (E_FAIL)",
            _ => $"Код {rc}"
        };

        return $"{reason}. {err}";
    }
}

[SupportedOSPlatform("windows")]
internal sealed class ShortcutProvisioner : IShortcutProvisioner
{
    private readonly LauncherPaths _paths;
    private readonly IInstalledClientLocator _clientLocator;
    private readonly ShortcutService _shortcutService;

    public ShortcutProvisioner(
        LauncherPaths paths,
        IInstalledClientLocator clientLocator,
        ShortcutService? shortcutService = null)
    {
        _paths = paths;
        _clientLocator = clientLocator;
        _shortcutService = shortcutService ?? new ShortcutService();
    }

    public ShortcutResult CreateOrUpdateShortcuts(AppConfig cfg, string? iconPath = null)
    {
        const string arguments = "--bootstrap --launch-client";
        var musicIcon = iconPath ?? _clientLocator.TryFindInstalledExe(cfg.InstallDir!);
        var launcherIcon = File.Exists(_paths.LauncherIconPath) ? _paths.LauncherIconPath : _paths.LauncherExePath;

        return _shortcutService.CreateOrUpdate(_paths.LauncherExePath, arguments, musicIcon, launcherIcon);
    }
}

internal sealed class AppConfigPersistence : IConfigPersistence
{
    public void Save(AppConfig cfg) => AppConfigStore.Save(cfg);
}

internal sealed class ClientLauncher : IClientLauncher
{
    public void Launch(string exePath)
    {
        var workingDir = Path.GetDirectoryName(exePath) ?? Directory.GetCurrentDirectory();
        var startInfo = new ProcessStartInfo(exePath)
        {
            WorkingDirectory = workingDir,
            UseShellExecute = true
        };

        Process.Start(startInfo);
    }
}
