using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Spectre.Console;
using YaLauncher.Native;
using YaLauncher.Services;
using YaLauncher.Storage;
using YaLauncher.Utils;

namespace YaLauncher;

[SupportedOSPlatform("windows")]
internal static class Program
{
    private const string LauncherVersion = "1.1.3";
    private static readonly string WorkDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "YaMusicLauncher",
        "work");
    private static readonly string SevenZipExe = Path.Combine(AppContext.BaseDirectory, "7zip", "7za.exe");
    private const int ParallelDownloads = 6;
    private static bool IntroAnimationShown;

    private const string ArgBootstrap = "--bootstrap";
    private const string ArgLaunchClient = "--launch-client";
    private const string ArgNoUpdate = "--no-update";
    private const string ArgElevated = "--elevated";

    private const string ArgRunInitialSetup = "--run-initial-setup";
    private const string ArgRunInstallClient = "--run-install-client";
    private const string ArgRunUpdateMod = "--run-update-mod";
    private const string ArgRunPatch = "--run-patch";
    private const string ArgRunCreateShortcuts = "--run-create-shortcuts";

    private static readonly IReadOnlySet<string> EmptyFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private enum MenuAction
    {
        InitialSetup,
        InitialSetupDisabled,
        InstallClient,
        UpdateMod,
        ShowLatestModVersion,
        PatchClient,
        RestoreBackup,
        DeleteBackups,
        CreateShortcuts,
        LaunchViaLauncher,
        Exit
    }

    private enum MenuSection
    {
        Core,
        InstallUpdate,
        Utilities,
        Settings,
        Exit
    }

    private sealed record InitialSetupResult(
        string InstallDir,
        string ExePath,
        ModInstallResult ModResult,
        int PatchedCount,
        ShortcutResult Shortcuts);

    private sealed record BootstrapResult(string ExePath, ModInstallResult? ModResult, int PatchedCount, string? Warning);

    static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.Title = $"YaMusic Launcher v{LauncherVersion}";

        var flags = new HashSet<string>(args, StringComparer.OrdinalIgnoreCase);
        var cfg = AppConfigStore.Load();

        try
        {
            if (flags.Contains(ArgBootstrap))
                return await RunBootstrapModeAsync(cfg, flags, args);

            if (flags.Contains(ArgRunInitialSetup))
                return await RunInitialSetupCommandAsync(cfg, flags);

            if (flags.Contains(ArgRunInstallClient))
                return await RunInstallClientCommandAsync(cfg, flags);

            if (flags.Contains(ArgRunUpdateMod))
                return await RunUpdateModCommandAsync(cfg, flags);

            if (flags.Contains(ArgRunPatch))
                return RunPatchCommand(cfg, flags);

            if (flags.Contains(ArgRunCreateShortcuts))
                return RunCreateShortcutsCommand(cfg, flags);

            return await RunInteractiveMenuAsync(cfg);
        }
        catch (Exception ex)
        {
            RedrawHeader();
            ShowUnhandledError(ex);
            return 1;
        }
    }

    private static async Task<int> RunInteractiveMenuAsync(AppConfig cfg)
    {
        while (true)
        {
            if (!IntroAnimationShown)
            {
                await ShowIntroAnimationAsync();
                IntroAnimationShown = true;
            }

            RedrawHeader();
            ShowLauncherStateSummary(cfg);
            if (!IsInitialSetupDoneForSelectedDir(cfg.InstallDir!))
            {
                AnsiConsole.MarkupLine("[yellow]Первичная установка еще не выполнена.[/]");
                AnsiConsole.MarkupLine("[grey]Рекомендуется сначала запустить пункт 'Первичная установка'.[/]");
                AnsiConsole.WriteLine();
            }

            var section = PromptMenuSection();
            if (section == MenuSection.Exit)
                return 0;

            if (section == MenuSection.Settings)
            {
                RunSettingsMenu(cfg);
                cfg = AppConfigStore.Load();
                continue;
            }

            var action = PromptMenuAction(section, cfg);
            if (action is null)
                continue;

            if (action == MenuAction.Exit)
                return 0;

            try
            {
                switch (action.Value)
                {
                    case MenuAction.InitialSetup:
                        if (!EnsureElevatedIfRequired(cfg.InstallDir!, [ArgRunInitialSetup], EmptyFlags))
                            return 0;
                        await RunInitialSetupInteractiveAsync(cfg);
                        break;

                    case MenuAction.InitialSetupDisabled:
                        RedrawHeader();
                        ShowInfoPanel(
                            "Пункт недоступен",
                            "Первичная установка уже выполнена для выбранной директории.\n" +
                            "Обнаружен установленный EXE Я.Музыки.\n\n" +
                            "Если нужно, используйте пункты переустановки/обновления.");
                        break;

                    case MenuAction.InstallClient:
                        if (!EnsureElevatedIfRequired(cfg.InstallDir!, [ArgRunInstallClient], EmptyFlags))
                            return 0;
                        await InstallClientOnlyInteractiveAsync(cfg);
                        break;

                    case MenuAction.UpdateMod:
                        if (!EnsureElevatedIfRequired(cfg.InstallDir!, [ArgRunUpdateMod], EmptyFlags))
                            return 0;
                        await UpdateModOnlyInteractiveAsync(cfg);
                        break;

                    case MenuAction.ShowLatestModVersion:
                        await ShowLatestModVersionInteractiveAsync(cfg);
                        break;

                    case MenuAction.PatchClient:
                        if (!EnsureElevatedIfRequired(cfg.InstallDir!, [ArgRunPatch], EmptyFlags))
                            return 0;
                        PatchOnlyInteractive(cfg);
                        break;

                    case MenuAction.RestoreBackup:
                        if (!EnsureElevatedIfRequired(cfg.InstallDir!, Array.Empty<string>(), EmptyFlags))
                            return 0;
                        RestoreBackupInteractive(cfg);
                        break;

                    case MenuAction.DeleteBackups:
                        if (!EnsureElevatedIfRequired(cfg.InstallDir!, Array.Empty<string>(), EmptyFlags))
                            return 0;
                        DeleteBackupsInteractive(cfg);
                        break;

                    case MenuAction.CreateShortcuts:
                        CreateShortcutsInteractive(cfg);
                        break;

                    case MenuAction.LaunchViaLauncher:
                        if (!EnsureElevatedIfRequired(cfg.InstallDir!, [ArgBootstrap, ArgLaunchClient], EmptyFlags))
                            return 0;
                        await RunBootstrapInteractiveAsync(cfg);
                        break;
                }
            }
            catch (Exception ex)
            {
                RedrawHeader();
                ShowUnhandledError(ex);
            }

            PauseAndContinue();
            cfg = AppConfigStore.Load();
        }
    }

    private static MenuSection PromptMenuSection()
    {
        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold]Главное меню[/]")
                .PageSize(10)
                .HighlightStyle(new Style(Color.Cyan1, decoration: Decoration.Bold))
                .WrapAround(true)
                .AddChoices(new[]
                {
                    "⚡ Основные действия",
                    "⬇️ Установка и обновление",
                    "🧰 Полезные утилиты",
                    "⚙️ Настройки",
                    "🚪 Выход"
                }));

        return selected switch
        {
            "⚡ Основные действия" => MenuSection.Core,
            "⬇️ Установка и обновление" => MenuSection.InstallUpdate,
            "🧰 Полезные утилиты" => MenuSection.Utilities,
            "⚙️ Настройки" => MenuSection.Settings,
            _ => MenuSection.Exit
        };
    }

    private static MenuAction? PromptMenuAction(MenuSection section, AppConfig cfg)
    {
        var hasInstalledClientInSelectedDir = IsInitialSetupDoneForSelectedDir(cfg.InstallDir!);
        var initialSetupChoice = hasInstalledClientInSelectedDir
            ? "[grey]🚀 Первичная установка (уже выполнена)[/]"
            : "🚀 Первичная установка (1/4 клиент -> 2/4 мод -> 3/4 патч -> 4/4 ярлыки)";

        var choices = section switch
        {
            MenuSection.Core => new[]
            {
                initialSetupChoice,
                "▶️ Запустить Я.Музыку через лаунчер",
                "◀️ Назад"
            },
            MenuSection.InstallUpdate => new[]
            {
                "⬇️ Переустановить клиент Я.Музыки",
                "🧩 Обновить мод (app.asar)",
                "🔧 Пропатчить установленный клиент",
                "🔗 Создать/обновить ярлыки",
                "◀️ Назад"
            },
            MenuSection.Utilities => new[]
            {
                "🔎 Показать версии мода и changelog (GitHub)",
                "📦 Восстановить мод из бэкапа",
                "🗑️ Удалить бэкапы",
                "◀️ Назад"
            },
            _ => ["◀️ Назад"]
        };

        var title = section switch
        {
            MenuSection.Core => "[bold]Основные действия[/]",
            MenuSection.InstallUpdate => "[bold]Установка и обновление[/]",
            MenuSection.Utilities => "[bold]Полезные утилиты[/]",
            _ => "[bold]Действия[/]"
        };

        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title(title)
                .PageSize(10)
                .HighlightStyle(new Style(Color.Cyan1, decoration: Decoration.Bold))
                .WrapAround(true)
                .AddChoices(choices));

        return selected switch
        {
            var x when x == initialSetupChoice => hasInstalledClientInSelectedDir
                ? MenuAction.InitialSetupDisabled
                : MenuAction.InitialSetup,
            "▶️ Запустить Я.Музыку через лаунчер" => MenuAction.LaunchViaLauncher,
            "⬇️ Переустановить клиент Я.Музыки" => MenuAction.InstallClient,
            "🧩 Обновить мод (app.asar)" => MenuAction.UpdateMod,
            "🔧 Пропатчить установленный клиент" => MenuAction.PatchClient,
            "🔗 Создать/обновить ярлыки" => MenuAction.CreateShortcuts,
            "🔎 Показать версии мода и changelog (GitHub)" => MenuAction.ShowLatestModVersion,
            "📦 Восстановить мод из бэкапа" => MenuAction.RestoreBackup,
            "🗑️ Удалить бэкапы" => MenuAction.DeleteBackups,
            _ => null
        };
    }

    private static async Task<int> RunInitialSetupCommandAsync(AppConfig cfg, IReadOnlySet<string> flags)
    {
        if (!EnsureElevatedIfRequired(cfg.InstallDir!, [ArgRunInitialSetup], flags))
            return 0;

        var result = await ExecuteInitialSetupAsync(cfg);
        RedrawHeader();
        ShowInitialSetupResult(result);
        return 0;
    }

    private static async Task<int> RunInstallClientCommandAsync(AppConfig cfg, IReadOnlySet<string> flags)
    {
        if (!EnsureElevatedIfRequired(cfg.InstallDir!, [ArgRunInstallClient], flags))
            return 0;

        var exe = await InstallLatestClientAsync(cfg.InstallDir!, ParallelDownloads);
        RedrawHeader();
        ShowInfoPanel("Клиент установлен", $"Каталог: {cfg.InstallDir}\nEXE: {exe}");
        return 0;
    }

    private static async Task<int> RunUpdateModCommandAsync(AppConfig cfg, IReadOnlySet<string> flags)
    {
        if (!EnsureElevatedIfRequired(cfg.InstallDir!, [ArgRunUpdateMod], flags))
            return 0;

        var updater = new ModClientUpdater();
        var processManager = new YandexProcessManager();
        await processManager.StopAllAsync();
        var result = await InstallLatestModAsync(cfg, updater, cfg.InstallDir!);

        RedrawHeader();
        ShowModUpdateResult(result);
        return 0;
    }

    private static int RunPatchCommand(AppConfig cfg, IReadOnlySet<string> flags)
    {
        if (!EnsureElevatedIfRequired(cfg.InstallDir!, [ArgRunPatch], flags))
            return 0;

        var exe = FindInstalledExeOrThrow(cfg.InstallDir!);
        var patched = ApplyPatchOrThrow(exe);

        RedrawHeader();
        ShowInfoPanel("Патч применен", $"EXE: {exe}\nОтключено участков: {patched}");
        return 0;
    }

    private static int RunCreateShortcutsCommand(AppConfig cfg, IReadOnlySet<string> flags)
    {
        if (!EnsureElevatedIfRequired(cfg.InstallDir!, [ArgRunCreateShortcuts], flags))
            return 0;

        var shortcuts = CreateOrUpdateShortcuts(cfg);
        RedrawHeader();
        ShowInfoPanel("Ярлыки готовы",
            $"Я.Музыка (Desktop): {shortcuts.MusicDesktopShortcutPath}\n" +
            $"Я.Музыка (Start Menu): {shortcuts.MusicStartMenuShortcutPath}\n" +
            $"Лаунчер (Desktop): {shortcuts.LauncherDesktopShortcutPath}\n" +
            $"Лаунчер (Start Menu): {shortcuts.LauncherStartMenuShortcutPath}");
        return 0;
    }

    private static async Task<int> RunBootstrapModeAsync(AppConfig cfg, IReadOnlySet<string> flags, string[] rawArgs)
    {
        RedrawHeader();

        if (!IsInitialSetupDoneForSelectedDir(cfg.InstallDir!))
        {
            ShowInfoPanel(
                "Запуск через ярлык недоступен",
                "Первичная установка не выполнена.\n" +
                "Запустите лаунчер в интерактивном режиме и выполните шаги установки.");
            return 2;
        }

        if (!EnsureElevatedIfRequired(cfg.InstallDir!, rawArgs, flags))
            return 0;

        var launchClient = flags.Contains(ArgLaunchClient);
        var noUpdate = flags.Contains(ArgNoUpdate);
        var owner = string.IsNullOrWhiteSpace(cfg.GitHubOwner) ? "TheKing-OfTime" : cfg.GitHubOwner;
        var repo = string.IsNullOrWhiteSpace(cfg.GitHubRepo) ? "YandexMusicModClient" : cfg.GitHubRepo;

        RedrawHeader();
        WriteBootstrapLog("Yandex Music Mod: запуск через лаунчер", "deepskyblue1");
        WriteBootstrapLog($"Каталог установки: {cfg.InstallDir}", "grey");
        WriteBootstrapLog(
            cfg.AutoUpdateBeforeLaunch && !noUpdate
                ? $"Проверка обновлений включена (GitHub: {owner}/{repo})"
                : noUpdate
                    ? "Проверка обновлений отключена флагом --no-update"
                    : "Проверка обновлений отключена в настройках",
            "grey");
        WriteBootstrapLog(
            launchClient
                ? "После проверки и патчинга клиент будет запущен автоматически"
                : "Режим без автозапуска клиента",
            "grey");
        AnsiConsole.WriteLine();

        var result = await ExecuteBootstrapAsync(cfg, launchClient, noUpdate, WriteBootstrapLog);
        if (!string.IsNullOrWhiteSpace(result.Warning))
            WriteBootstrapLog(result.Warning!, "yellow");

        WriteBootstrapLog("Последовательность запуска завершена.", "green");

        return 0;
    }

    private static async Task RunInitialSetupInteractiveAsync(AppConfig cfg)
    {
        var result = await ExecuteInitialSetupAsync(cfg);
        RedrawHeader();
        ShowInitialSetupResult(result);
    }

    private static async Task InstallClientOnlyInteractiveAsync(AppConfig cfg)
    {
        var exe = await InstallLatestClientAsync(cfg.InstallDir!, ParallelDownloads);
        RedrawHeader();
        ShowInfoPanel("Клиент переустановлен", $"Каталог: {cfg.InstallDir}\nEXE: {exe}");
    }

    private static async Task UpdateModOnlyInteractiveAsync(AppConfig cfg)
    {
        var updater = new ModClientUpdater();
        var processManager = new YandexProcessManager();
        await processManager.StopAllAsync();
        var result = await InstallLatestModAsync(cfg, updater, cfg.InstallDir!);

        RedrawHeader();
        ShowModUpdateResult(result);
    }

    private static async Task ShowLatestModVersionInteractiveAsync(AppConfig cfg)
    {
        var updater = new ModClientUpdater();
        var owner = string.IsNullOrWhiteSpace(cfg.GitHubOwner) ? "TheKing-OfTime" : cfg.GitHubOwner;
        var repo = string.IsNullOrWhiteSpace(cfg.GitHubRepo) ? "YandexMusicModClient" : cfg.GitHubRepo;
        var releases = await updater.GetRecentReleasesAsync(owner, repo, limit: 8);

        RedrawHeader();
        if (releases.Count == 0)
        {
            ShowInfoPanel("Версия мода", $"Не удалось получить релизы с GitHub: {owner}/{repo}");
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"GitHub: {owner}/{repo}");
        sb.AppendLine($"Последняя версия: {releases[0].Tag}");
        sb.AppendLine();
        sb.AppendLine("История изменений по версиям:");
        sb.AppendLine();

        for (var i = 0; i < releases.Count; i++)
        {
            var release = releases[i];
            sb.AppendLine(FormatReleaseTitle(i + 1, release));

            var body = BuildReleaseBodyPreview(release.Body, maxLines: 8, maxChars: 900);
            if (string.IsNullOrWhiteSpace(body))
            {
                sb.AppendLine("  Описание изменений отсутствует.");
            }
            else
            {
                foreach (var line in body.Split('\n'))
                    sb.AppendLine($"  {line}");
            }

            if (i < releases.Count - 1)
                sb.AppendLine();
        }

        ShowInfoPanel("Версии мода и changelog", sb.ToString().TrimEnd());
    }

    private static void PatchOnlyInteractive(AppConfig cfg)
    {
        var exe = FindInstalledExeOrThrow(cfg.InstallDir!);
        var patched = ApplyPatchOrThrow(exe);

        RedrawHeader();
        ShowInfoPanel("Патч применен", $"EXE: {exe}\nОтключено участков: {patched}");
    }

    private static void RestoreBackupInteractive(AppConfig cfg)
    {
        var updater = new ModClientUpdater();
        var backups = updater.ListBackups(cfg.InstallDir!, cfg.BackupAutoCleanupLimitMb);
        if (backups.Count == 0)
        {
            RedrawHeader();
            ShowInfoPanel("Бэкапов нет", "Каталог backups_app пуст.");
            return;
        }

        var choices = backups.Select((b, i) => $"{i + 1}) {b.FileName} ({b.CreatedAt:yyyy-MM-dd HH:mm:ss})").ToList();
        choices.Add("0) Назад");

        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold]Выберите бэкап[/]")
                .PageSize(15)
                .AddChoices(choices));

        if (selected.StartsWith("0)", StringComparison.Ordinal))
            return;

        var indexText = selected.Split(')', 2)[0];
        if (!int.TryParse(indexText, out var index) || index < 1 || index > backups.Count)
            throw new InvalidOperationException("Не удалось распознать выбранный бэкап.");

        var chosen = backups[index - 1];
        var processManager = new YandexProcessManager();
        processManager.StopAllAsync().GetAwaiter().GetResult();
        updater.RestoreBackup(cfg.InstallDir!, chosen.FullPath, cfg.BackupAutoCleanupLimitMb);

        RedrawHeader();
        ShowInfoPanel("Мод восстановлен", $"Источник: {chosen.FileName}");
    }

    private static void DeleteBackupsInteractive(AppConfig cfg)
    {
        var updater = new ModClientUpdater();
        var installDir = cfg.InstallDir!;
        var backups = updater.ListBackups(installDir, cfg.BackupAutoCleanupLimitMb);
        if (backups.Count == 0)
        {
            RedrawHeader();
            ShowInfoPanel("Бэкапов нет", "Каталог backups_app пуст.");
            return;
        }

        var totalBefore = updater.GetBackupDirectorySizeBytes(installDir);
        var mode = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold]Удаление бэкапов[/]")
                .PageSize(8)
                .AddChoices(new[]
                {
                    "🗑️ Удалить один бэкап",
                    "♻️ Очистить все бэкапы",
                    "◀️ Назад"
                }));

        if (mode == "◀️ Назад")
            return;

        if (mode == "♻️ Очистить все бэкапы")
        {
            if (!AnsiConsole.Confirm("Удалить [red]все[/] бэкапы?"))
                return;

            var removedFiles = updater.DeleteAllBackups(installDir);
            var totalAfter = updater.GetBackupDirectorySizeBytes(installDir);
            RedrawHeader();
            ShowInfoPanel(
                "Бэкапы удалены",
                $"Удалено файлов: {removedFiles}\n" +
                $"Было: {FormatBytes(totalBefore)}\n" +
                $"Стало: {FormatBytes(totalAfter)}");
            return;
        }

        var choices = backups
            .Select((b, i) =>
            {
                long size = 0;
                try { size = new FileInfo(b.FullPath).Length; } catch { }
                return $"{i + 1}) {b.FileName} ({b.CreatedAt:yyyy-MM-dd HH:mm:ss}, {FormatBytes(size)})";
            })
            .ToList();
        choices.Add("0) Назад");

        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold]Выберите бэкап для удаления[/]")
                .PageSize(15)
                .AddChoices(choices));

        if (selected.StartsWith("0)", StringComparison.Ordinal))
            return;

        var indexText = selected.Split(')', 2)[0];
        if (!int.TryParse(indexText, out var index) || index < 1 || index > backups.Count)
            throw new InvalidOperationException("Не удалось распознать выбранный бэкап.");

        var chosen = backups[index - 1];
        if (!AnsiConsole.Confirm($"Удалить бэкап [red]{Markup.Escape(chosen.FileName)}[/]?"))
            return;

        var deleted = updater.DeleteBackup(installDir, chosen.FullPath);
        var totalAfterDelete = updater.GetBackupDirectorySizeBytes(installDir);

        RedrawHeader();
        ShowInfoPanel(
            deleted ? "Бэкап удален" : "Удаление не выполнено",
            $"Файл: {chosen.FileName}\n" +
            $"Было: {FormatBytes(totalBefore)}\n" +
            $"Стало: {FormatBytes(totalAfterDelete)}");
    }

    private static void CreateShortcutsInteractive(AppConfig cfg)
    {
        var shortcuts = CreateOrUpdateShortcuts(cfg);
        RedrawHeader();
        ShowInfoPanel("Ярлыки обновлены",
            $"Я.Музыка (Desktop): {shortcuts.MusicDesktopShortcutPath}\n" +
            $"Я.Музыка (Start Menu): {shortcuts.MusicStartMenuShortcutPath}\n" +
            $"Лаунчер (Desktop): {shortcuts.LauncherDesktopShortcutPath}\n" +
            $"Лаунчер (Start Menu): {shortcuts.LauncherStartMenuShortcutPath}");
    }

    private static async Task RunBootstrapInteractiveAsync(AppConfig cfg)
    {
        if (!IsInitialSetupDoneForSelectedDir(cfg.InstallDir!))
            throw new InvalidOperationException("Первичная установка не завершена.");

        var result = await ExecuteBootstrapAsync(cfg, launchClient: true, noUpdate: false);

        RedrawHeader();
        ShowInfoPanel("Запуск через лаунчер выполнен",
            $"EXE: {result.ExePath}\nПатч участков: {result.PatchedCount}" +
            (string.IsNullOrWhiteSpace(result.Warning) ? string.Empty : $"\nПредупреждение: {result.Warning}"));
    }

    private static async Task<InitialSetupResult> ExecuteInitialSetupAsync(AppConfig cfg)
    {
        EnsureSevenZip();
        var installDir = cfg.InstallDir!;

        var processManager = new YandexProcessManager();
        await processManager.StopAllAsync();

        PrintSetupStage("Шаг 1/4: скачивание и установка клиента Я.Музыки");
        var exePath = await InstallLatestClientAsync(installDir, ParallelDownloads, stagePrefix: "1/4");

        PrintSetupStage("Шаг 2/4: скачивание и установка модифицированного app.asar");
        var updater = new ModClientUpdater();
        var modResult = await InstallLatestModAsync(cfg, updater, installDir, stagePrefix: "2/4");

        PrintSetupStage("Шаг 3/4: патчинг клиента через AsarFusePatcher.dll");
        var patchedCount = ApplyPatchOrThrow(exePath);

        PrintSetupStage("Шаг 4/4: создание ярлыков");
        var shortcuts = CreateOrUpdateShortcuts(cfg, exePath);

        cfg.IsInitialSetupCompleted = true;
        AppConfigStore.Save(cfg);

        return new InitialSetupResult(installDir, exePath, modResult, patchedCount, shortcuts);
    }

    private static async Task<BootstrapResult> ExecuteBootstrapAsync(
        AppConfig cfg,
        bool launchClient,
        bool noUpdate,
        Action<string, string>? log = null)
    {
        log?.Invoke("Проверяем окружение лаунчера...", "cyan");
        EnsureSevenZip();
        var installDir = cfg.InstallDir!;
        if (!Directory.Exists(installDir))
            throw new DirectoryNotFoundException($"Каталог установки не найден: {installDir}");

        log?.Invoke("Ищем установленный клиент Я.Музыки...", "cyan");
        var exePath = FindInstalledExeOrThrow(installDir);

        log?.Invoke("Останавливаем процессы Я.Музыки...", "cyan");
        var processManager = new YandexProcessManager();
        await processManager.StopAllAsync();
        log?.Invoke("Процессы остановлены.", "green");

        ModInstallResult? modResult = null;
        string? warning = null;
        var owner = string.IsNullOrWhiteSpace(cfg.GitHubOwner) ? "TheKing-OfTime" : cfg.GitHubOwner;
        var repo = string.IsNullOrWhiteSpace(cfg.GitHubRepo) ? "YandexMusicModClient" : cfg.GitHubRepo;
        if (cfg.AutoUpdateBeforeLaunch && !noUpdate)
        {
            log?.Invoke($"Проверяем актуальную версию мода на GitHub ({owner}/{repo})...", "cyan");
            try
            {
                var updater = new ModClientUpdater();
                modResult = await updater.InstallLatestAsync(
                    installDir,
                    owner,
                    repo,
                    cfg.BackupAutoCleanupLimitMb);

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

        log?.Invoke("Применяем DLL-патч клиента...", "cyan");
        var patchedCount = ApplyPatchOrThrow(exePath);
        log?.Invoke($"Патч применен. Изменено участков: {patchedCount}", "green");

        if (launchClient)
        {
            log?.Invoke("Запускаем клиент Я.Музыки...", "cyan");
            LaunchClient(exePath);
            log?.Invoke("Клиент запущен.", "green");
        }

        return new BootstrapResult(exePath, modResult, patchedCount, warning);
    }

    private static async Task<string> InstallLatestClientAsync(string installDir, int parallel, string stagePrefix = "")
    {
        EnsureSevenZip();
        ValidateInstallPathForDelete(installDir);

        string archivePath = string.Empty;
        string locatedExe = string.Empty;

        var downloader = new YandexMusicDownloader();
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
                var tCleanup = ctx.AddTask(BuildStageTaskMarkup(stagePrefix, "Подготовка каталога установки", "grey"), maxValue: 1, autoStart: true);
                var tDownload = ctx.AddTask(BuildStageTaskMarkup(stagePrefix, "Загрузка клиента Я.Музыки", "cyan"), autoStart: false);
                var tExtract = ctx.AddTask(BuildStageTaskMarkup(stagePrefix, "Распаковка клиента", "yellow"), maxValue: 100, autoStart: false);

                if (Directory.Exists(installDir))
                    SafeDelete.DeleteDirectory(installDir);
                Directory.CreateDirectory(installDir);
                tCleanup.Value = 1;

                tDownload.StartTask();
                var dlProgress = new Progress<DownloadProgress>(p =>
                {
                    if (p.TotalBytes > 0 && tDownload.MaxValue == 100)
                        tDownload.MaxValue = Math.Max(1, p.TotalBytes);

                    tDownload.Value = p.ReceivedBytes;
                    if (p.TotalBytes > 0)
                    {
                        var description = $"{BuildStageTaskTitle(stagePrefix, "Загрузка клиента Я.Музыки")} ({FormatBytes(p.ReceivedBytes)}/{FormatBytes(p.TotalBytes)})";
                        tDownload.Description =
                            $"[cyan]{Markup.Escape(description)}[/]";
                    }
                });

                (archivePath, _) = await downloader.DownloadLatestAsync(WorkDir, parallel, dlProgress);
                tDownload.Value = tDownload.MaxValue;

                tExtract.StartTask();
                await SevenZipExtractor.ExtractAsync(SevenZipExe, archivePath, installDir,
                    progress: new Progress<double>(p => tExtract.Value = p), ct: default);
                tExtract.Value = tExtract.MaxValue;

                locatedExe = ExecutableFinder.FindExe(installDir);
            });

        return locatedExe;
    }

    private static async Task<ModInstallResult> InstallLatestModAsync(AppConfig cfg, ModClientUpdater updater, string installDir, string stagePrefix = "")
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
                var progress = new Progress<DownloadProgress>(p =>
                {
                    if (p.TotalBytes > 0 && task.MaxValue == 100)
                        task.MaxValue = Math.Max(1, p.TotalBytes);
                    task.Value = p.ReceivedBytes;
                });

                result = await updater.InstallLatestAsync(
                    installDir,
                    cfg.GitHubOwner,
                    cfg.GitHubRepo,
                    cfg.BackupAutoCleanupLimitMb,
                    progress);
                task.Value = task.MaxValue;
            });

        return result ?? throw new InvalidOperationException("Не удалось получить результат обновления мода.");
    }

    private static int ApplyPatchOrThrow(string exePath)
    {
        var dry = FuseLib.Disable(exePath, dryRun: true, limit: -1, out var dryError);
        if (dry < 0)
            throw new InvalidOperationException(DescribeFuseFailure(dry, dryError));

        var rc = FuseLib.Disable(exePath, dryRun: false, limit: -1, out var applyError);
        if (rc < 0)
            throw new InvalidOperationException(DescribeFuseFailure(rc, applyError));

        return rc;
    }

    private static ShortcutResult CreateOrUpdateShortcuts(AppConfig cfg, string? iconPath = null)
    {
        var launcherPath = Environment.ProcessPath
                           ?? throw new InvalidOperationException("Не удалось определить путь текущего EXE.");
        var arguments = $"{ArgBootstrap} {ArgLaunchClient}";
        var service = new ShortcutService();
        var exe = iconPath ?? TryFindInstalledExe(cfg.InstallDir!);
        return service.CreateOrUpdate(launcherPath, arguments, exe);
    }

    private static string BuildStageTaskTitle(string stagePrefix, string title) =>
        string.IsNullOrWhiteSpace(stagePrefix) ? title : $"Шаг {stagePrefix}: {title}";

    private static string BuildStageTaskMarkup(string stagePrefix, string title, string color) =>
        $"[{color}]{Markup.Escape(BuildStageTaskTitle(stagePrefix, title))}[/]";

    private static string FormatReleaseTitle(int index, ModReleaseInfo release)
    {
        var published = release.PublishedAt?.LocalDateTime.ToString("yyyy-MM-dd") ?? "unknown date";
        var pre = release.IsPreRelease ? " [pre-release]" : string.Empty;

        if (!string.IsNullOrWhiteSpace(release.Name) &&
            !string.Equals(release.Name, release.Tag, StringComparison.OrdinalIgnoreCase))
        {
            return $"{index}) {release.Tag} - {release.Name} ({published}){pre}";
        }

        return $"{index}) {release.Tag} ({published}){pre}";
    }

    private static string BuildReleaseBodyPreview(string? body, int maxLines, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(body))
            return string.Empty;

        var normalized = body.Replace("\r\n", "\n").Replace("\r", "\n");
        var lines = normalized
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeReleaseLine)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();

        var clippedByLines = lines.Count > maxLines;
        var selected = lines.Take(maxLines).ToArray();
        var text = string.Join("\n", selected);

        var clippedByChars = text.Length > maxChars;
        if (clippedByChars)
            text = text[..maxChars].TrimEnd();

        if (clippedByLines || clippedByChars)
            text += "\n...";

        return text;
    }

    private static string NormalizeReleaseLine(string line)
    {
        var text = line.Trim();
        while (text.StartsWith("#", StringComparison.Ordinal))
            text = text[1..].TrimStart();

        if (text.StartsWith("- ", StringComparison.Ordinal) || text.StartsWith("* ", StringComparison.Ordinal))
            text = text[2..].TrimStart();

        return text;
    }

    private static void PrintSetupStage(string text)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold blue]{Markup.Escape(text)}[/]");
    }

    private static string FindInstalledExeOrThrow(string installDir)
    {
        if (!Directory.Exists(installDir))
            throw new DirectoryNotFoundException($"Каталог установки не найден: {installDir}");
        return ExecutableFinder.FindExe(installDir);
    }

    private static bool IsInitialSetupDoneForSelectedDir(string installDir)
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

    private static string? TryFindInstalledExe(string installDir)
    {
        try { return FindInstalledExeOrThrow(installDir); }
        catch { return null; }
    }

    private static void LaunchClient(string exePath)
    {
        var workingDir = Path.GetDirectoryName(exePath) ?? Directory.GetCurrentDirectory();
        var psi = new ProcessStartInfo(exePath)
        {
            WorkingDirectory = workingDir,
            UseShellExecute = true
        };
        Process.Start(psi);
    }

    private static bool EnsureElevatedIfRequired(string installDir, IEnumerable<string> relaunchArgs, IReadOnlySet<string> currentFlags)
    {
        if (IsRunningAsAdministrator())
            return true;

        if (CanWriteToInstallDir(installDir))
            return true;

        if (currentFlags.Contains(ArgElevated))
            throw new UnauthorizedAccessException("Недостаточно прав на запись в каталог установки.");

        var args = relaunchArgs.Concat([ArgElevated]).ToArray();
        if (!TryRelaunchElevated(args))
            throw new InvalidOperationException("Не удалось запустить elevated процесс. Операция отменена.");

        return false;
    }

    private static bool TryRelaunchElevated(string[] args)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
            return false;

        var argLine = string.Join(" ", args.Select(EscapeArgument));
        var psi = new ProcessStartInfo(exePath)
        {
            Arguments = argLine,
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? Directory.GetCurrentDirectory()
        };

        try
        {
            Process.Start(psi);
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // UAC canceled by user
            return false;
        }
    }

    private static bool IsRunningAsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool CanWriteToInstallDir(string installDir)
    {
        try
        {
            Directory.CreateDirectory(installDir);
            var probe = Path.Combine(installDir, $".write_probe_{Guid.NewGuid():N}.tmp");
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1,
                       FileOptions.DeleteOnClose))
            {
                stream.WriteByte(1);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void ValidateInstallPathForDelete(string installDir)
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

    private static string EscapeArgument(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "\"\"";

        if (!value.Any(char.IsWhiteSpace) && !value.Contains('"'))
            return value;

        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    private static void RunSettingsMenu(AppConfig cfg)
    {
        while (true)
        {
            RedrawHeader();

            var state = cfg.AutoUpdateBeforeLaunch ? "включено" : "выключено";
            var setupDone = IsInitialSetupDoneForSelectedDir(cfg.InstallDir!);
            var backupsLimitText = cfg.BackupAutoCleanupLimitMb <= 0
                ? "выключена"
                : $"{cfg.BackupAutoCleanupLimitMb} МБ";
            AnsiConsole.MarkupLine($"[grey]Каталог установки:[/] {Markup.Escape(cfg.InstallDir ?? "-")}");
            AnsiConsole.MarkupLine($"[grey]Auto-update перед запуском:[/] {state}");
            AnsiConsole.MarkupLine($"[grey]Первичная установка:[/] {(setupDone ? "выполнена" : "не выполнена")}");
            AnsiConsole.MarkupLine($"[grey]Авто-очистка бэкапов:[/] {backupsLimitText}");
            AnsiConsole.WriteLine();

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold]Настройки[/]")
                    .PageSize(8)
                    .HighlightStyle(new Style(Color.Yellow, decoration: Decoration.Bold))
                    .WrapAround(true)
                    .AddChoices(new[]
                    {
                        "✏️ Изменить путь установки",
                        "↩️ Сбросить путь к стандартному",
                        "🔁 Переключить auto-update перед запуском",
                        "🧹 Лимит авто-очистки бэкапов (МБ)",
                        "◀️ Назад"
                    }));

            if (choice == "◀️ Назад")
                return;

            if (choice == "✏️ Изменить путь установки")
            {
                var enteredPath = AnsiConsole.Ask<string>("Введите [cyan]полный путь[/] каталога установки:");
                if (string.IsNullOrWhiteSpace(enteredPath))
                    continue;

                var full = Path.GetFullPath(enteredPath.Trim());
                if (!Directory.Exists(full))
                {
                    var create = AnsiConsole.Confirm($"Каталог '{full}' не существует. Создать?");
                    if (!create)
                        continue;
                    Directory.CreateDirectory(full);
                }

                cfg.InstallDir = full;
                AppConfigStore.Save(cfg);
                continue;
            }

            if (choice == "↩️ Сбросить путь к стандартному")
            {
                cfg.InstallDir = GetDefaultInstallDir();
                AppConfigStore.Save(cfg);
                continue;
            }

            if (choice == "🔁 Переключить auto-update перед запуском")
            {
                cfg.AutoUpdateBeforeLaunch = !cfg.AutoUpdateBeforeLaunch;
                AppConfigStore.Save(cfg);
                continue;
            }

            if (choice == "🧹 Лимит авто-очистки бэкапов (МБ)")
            {
                var value = AnsiConsole.Ask<int>(
                    "Введите лимит в МБ ([grey]0 — отключить авто-очистку[/]):",
                    cfg.BackupAutoCleanupLimitMb);

                if (value < 0)
                    value = 0;

                cfg.BackupAutoCleanupLimitMb = value;
                AppConfigStore.Save(cfg);
                continue;
            }
        }
    }

    private static string GetDefaultInstallDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "YandexMusic");

    private static void EnsureSevenZip()
    {
        if (!File.Exists(SevenZipExe))
            throw new FileNotFoundException($"7za.exe не найден по пути: {SevenZipExe}");
    }

    private static void PauseAndContinue()
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]Нажмите любую клавишу, чтобы вернуться в меню…[/]");
        Console.ReadKey(true);
    }

    private static void RedrawHeader()
    {
        Console.Clear();
        var logo = FiggleFonts.Slant.Render("YaMusic Launcher");
        AnsiConsole.Write(new Text(logo, new Style(Color.Green, decoration: Decoration.Bold)));
        AnsiConsole.MarkupLine($"[bold blue]by m1ndst0rm v{Markup.Escape(LauncherVersion)}[/]");
        AnsiConsole.Write(new Rule("[grey]────────────────────────────────────────[/]").RuleStyle("grey").LeftJustified());
        AnsiConsole.WriteLine();
    }

    private static string FormatBytes(long v)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double x = v;
        var i = 0;
        while (x >= 1024 && i < units.Length - 1)
        {
            x /= 1024;
            i++;
        }

        return $"{x:0.##} {units[i]}";
    }

    private static void ShowInfoPanel(string title, string body)
    {
        var panel = new Panel(Markup.Escape(body))
            .Header($"[bold green]{Markup.Escape(title)}[/]")
            .Border(BoxBorder.Rounded);
        AnsiConsole.Write(panel);
    }

    private static void ShowInitialSetupResult(InitialSetupResult result)
    {
        var modLine = result.ModResult.Updated
            ? $"обновлен до {result.ModResult.LatestVersion ?? "unknown"}"
            : $"без изменений ({result.ModResult.InstalledVersion ?? "unknown"})";

        ShowInfoPanel(
            "Первичная установка завершена",
            $"Каталог: {result.InstallDir}\n" +
            $"Шаг 1: клиент установлен ({result.ExePath})\n" +
            $"Шаг 2: мод {modLine}\n" +
            $"Шаг 3: патч применен, участков: {result.PatchedCount}\n" +
            $"Шаг 4: ярлыки созданы\n" +
            $"  - Я.Музыка Desktop: {result.Shortcuts.MusicDesktopShortcutPath}\n" +
            $"  - Я.Музыка Start Menu: {result.Shortcuts.MusicStartMenuShortcutPath}\n" +
            $"  - Лаунчер Desktop: {result.Shortcuts.LauncherDesktopShortcutPath}\n" +
            $"  - Лаунчер Start Menu: {result.Shortcuts.LauncherStartMenuShortcutPath}");
    }

    private static void ShowModUpdateResult(ModInstallResult result)
    {
        if (result.Updated)
        {
            ShowInfoPanel(
                "Мод обновлен",
                $"Было: {result.InstalledVersion ?? "unknown"}\n" +
                $"Стало: {result.LatestVersion ?? "unknown"}");
            return;
        }

        ShowInfoPanel(
            "Обновление не требуется",
            $"Текущая версия: {result.InstalledVersion ?? "unknown"}");
    }

    private static void ShowUnhandledError(Exception ex)
    {
        var panel = new Panel(
                $"[red]{Markup.Escape(ex.Message)}[/]\n[grey]{Markup.Escape(ex.GetType().Name)}[/]")
            .Header("[bold red]Ошибка[/]")
            .Border(BoxBorder.Rounded);
        AnsiConsole.Write(panel);
    }

    private static void WriteBootstrapLog(string message, string color)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        AnsiConsole.MarkupLine($"[{color}]{Markup.Escape(line)}[/]");
    }

    private static async Task ShowIntroAnimationAsync()
    {
        Console.Clear();
        var lines = FiggleFonts.Slant.Render("YaMusic Launcher")
            .Split(Environment.NewLine, StringSplitOptions.None);

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                Console.WriteLine();
                continue;
            }

            AnsiConsole.MarkupLine($"[green]{Markup.Escape(line)}[/]");
            await Task.Delay(28);
        }

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Star)
            .SpinnerStyle(Style.Parse("cyan"))
            .StartAsync("Инициализация меню и модулей...", async _ =>
            {
                await Task.Delay(450);
            });
    }

    private static void ShowLauncherStateSummary(AppConfig cfg)
    {
        var setupDone = IsInitialSetupDoneForSelectedDir(cfg.InstallDir!);
        var statusColor = setupDone ? "green" : "yellow";
        var statusText = setupDone ? "готово" : "требуется";
        var autoUpdate = cfg.AutoUpdateBeforeLaunch ? "вкл" : "выкл";
        var backupLimit = cfg.BackupAutoCleanupLimitMb <= 0
            ? "выкл"
            : $"{cfg.BackupAutoCleanupLimitMb} МБ";

        var body =
            $"[grey]Установка:[/] [white]{Markup.Escape(cfg.InstallDir ?? "-")}[/]\n" +
            $"[grey]Первичная настройка:[/] [{statusColor}]{statusText}[/]\n" +
            $"[grey]Auto-update:[/] [white]{autoUpdate}[/]\n" +
            $"[grey]Лимит бэкапов:[/] [white]{backupLimit}[/]\n" +
            $"[grey]GitHub мод:[/] [white]{Markup.Escape(cfg.GitHubOwner)}/{Markup.Escape(cfg.GitHubRepo)}[/]";

        var panel = new Panel(new Markup(body))
            .Header("[bold]Состояние лаунчера[/]")
            .Border(BoxBorder.Rounded)
            .Expand();
        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
    }
}
