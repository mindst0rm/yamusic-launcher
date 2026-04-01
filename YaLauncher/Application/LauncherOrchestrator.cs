using YaLauncher.Services;
using YaLauncher.Storage;

namespace YaLauncher.Application;

internal sealed class LauncherOrchestrator
{
    private readonly ILauncherPrerequisites _prerequisites;
    private readonly IProcessController _processController;
    private readonly IInstalledClientLocator _clientLocator;
    private readonly IClientInstallationService _clientInstallationService;
    private readonly IModInstallationService _modInstallationService;
    private readonly IPatchService _patchService;
    private readonly IShortcutProvisioner _shortcutProvisioner;
    private readonly IConfigPersistence _configPersistence;
    private readonly IClientLauncher _clientLauncher;

    public LauncherOrchestrator(
        ILauncherPrerequisites prerequisites,
        IProcessController processController,
        IInstalledClientLocator clientLocator,
        IClientInstallationService clientInstallationService,
        IModInstallationService modInstallationService,
        IPatchService patchService,
        IShortcutProvisioner shortcutProvisioner,
        IConfigPersistence configPersistence,
        IClientLauncher clientLauncher)
    {
        _prerequisites = prerequisites;
        _processController = processController;
        _clientLocator = clientLocator;
        _clientInstallationService = clientInstallationService;
        _modInstallationService = modInstallationService;
        _patchService = patchService;
        _shortcutProvisioner = shortcutProvisioner;
        _configPersistence = configPersistence;
        _clientLauncher = clientLauncher;
    }

    public Task<string> InstallClientAsync(AppConfig cfg, int parallelDownloads, CancellationToken ct = default)
    {
        _prerequisites.EnsureSevenZipAvailable();
        _prerequisites.ValidateInstallPathForDelete(cfg.InstallDir!);
        return _clientInstallationService.InstallLatestClientAsync(cfg.InstallDir!, parallelDownloads, ct: ct);
    }

    public async Task<ModInstallResult> UpdateModAsync(AppConfig cfg, CancellationToken ct = default)
    {
        await _processController.StopAllAsync(ct);
        return await _modInstallationService.InstallLatestModAsync(cfg, cfg.InstallDir!, ct: ct);
    }

    public int PatchClient(AppConfig cfg)
    {
        var exePath = _clientLocator.FindInstalledExeOrThrow(cfg.InstallDir!);
        return _patchService.ApplyPatchOrThrow(exePath);
    }

    public ShortcutResult CreateShortcuts(AppConfig cfg) =>
        _shortcutProvisioner.CreateOrUpdateShortcuts(cfg);

    public bool IsInitialSetupDone(string installDir) =>
        _clientLocator.IsInitialSetupDone(installDir);

    public async Task<InitialSetupResult> ExecuteInitialSetupAsync(
        AppConfig cfg,
        int parallelDownloads,
        Action<string>? printStage = null,
        CancellationToken ct = default)
    {
        _prerequisites.EnsureSevenZipAvailable();
        _prerequisites.ValidateInstallPathForDelete(cfg.InstallDir!);

        await _processController.StopAllAsync(ct);

        printStage?.Invoke("Шаг 1/4: скачивание и установка клиента Я.Музыки");
        var exePath = await _clientInstallationService.InstallLatestClientAsync(
            cfg.InstallDir!,
            parallelDownloads,
            stagePrefix: "1/4",
            ct: ct);

        printStage?.Invoke("Шаг 2/4: скачивание и установка модифицированного app.asar");
        var modResult = await _modInstallationService.InstallLatestModAsync(
            cfg,
            cfg.InstallDir!,
            stagePrefix: "2/4",
            ct: ct);

        printStage?.Invoke("Шаг 3/4: патчинг клиента через AsarFusePatcher.dll");
        var patchedCount = _patchService.ApplyPatchOrThrow(exePath);

        printStage?.Invoke("Шаг 4/4: создание ярлыков");
        var shortcuts = _shortcutProvisioner.CreateOrUpdateShortcuts(cfg, exePath);

        cfg.IsInitialSetupCompleted = true;
        _configPersistence.Save(cfg);

        return new InitialSetupResult(cfg.InstallDir!, exePath, modResult, patchedCount, shortcuts);
    }

    public async Task<BootstrapResult> ExecuteBootstrapAsync(
        AppConfig cfg,
        bool launchClient,
        bool noUpdate,
        Action<string, string>? log = null,
        CancellationToken ct = default)
    {
        log?.Invoke("Проверяем окружение лаунчера...", "cyan");
        _prerequisites.EnsureSevenZipAvailable();

        var installDir = cfg.InstallDir!;
        if (!Directory.Exists(installDir))
            throw new DirectoryNotFoundException($"Каталог установки не найден: {installDir}");

        log?.Invoke("Ищем установленный клиент Я.Музыки...", "cyan");
        var exePath = _clientLocator.FindInstalledExeOrThrow(installDir);

        log?.Invoke("Останавливаем процессы Я.Музыки...", "cyan");
        await _processController.StopAllAsync(ct);
        log?.Invoke("Процессы остановлены.", "green");

        ModInstallResult? modResult = null;
        string? warning = null;
        var repoDisplay = GetRepoDisplay(cfg);

        if (cfg.AutoUpdateBeforeLaunch && !noUpdate)
        {
            log?.Invoke($"Проверяем актуальную версию мода на GitHub ({repoDisplay})...", "cyan");
            try
            {
                modResult = await _modInstallationService.InstallLatestModAsync(cfg, installDir, ct: ct);

                if (modResult.Updated)
                {
                    log?.Invoke(
                        $"Мод обновлен: {modResult.InstalledVersion ?? "unknown"} -> {modResult.LatestVersion ?? "unknown"}",
                        "green");
                }
                else
                {
                    log?.Invoke(
                        $"Мод актуален: {modResult.InstalledVersion ?? modResult.LatestVersion ?? "unknown"}",
                        "green");
                }
            }
            catch (Exception ex)
            {
                warning = $"Не удалось обновить мод-клиент: {ex.Message}";
                log?.Invoke(warning, "yellow");
            }
        }
        else
        {
            var reason = noUpdate ? "флаг --no-update" : "настройка auto-update выключена";
            log?.Invoke($"Шаг проверки обновлений пропущен ({reason}).", "grey");
        }

        var patchedCount = 0;
        if (modResult?.Updated == true)
        {
            log?.Invoke("Обновление найдено, применяем DLL-патч клиента...", "cyan");
            patchedCount = _patchService.ApplyPatchOrThrow(exePath);
            log?.Invoke($"Патч применен. Изменено участков: {patchedCount}", "green");
        }
        else
        {
            var reason = modResult is { Updated: false }
                ? "обновлений мода нет"
                : "обновление не выполнялось";
            log?.Invoke($"Шаг патчинга пропущен ({reason}).", "grey");
        }

        if (launchClient)
        {
            log?.Invoke("Запускаем клиент Я.Музыки...", "cyan");
            _clientLauncher.Launch(exePath);
            log?.Invoke("Клиент запущен.", "green");
        }

        return new BootstrapResult(exePath, modResult, patchedCount, warning);
    }

    private static string GetRepoDisplay(AppConfig cfg)
    {
        var owner = string.IsNullOrWhiteSpace(cfg.GitHubOwner) ? AppConfig.DefaultGitHubOwner : cfg.GitHubOwner;
        var repo = string.IsNullOrWhiteSpace(cfg.GitHubRepo) ? AppConfig.DefaultGitHubRepo : cfg.GitHubRepo;
        return $"{owner}/{repo}";
    }
}
