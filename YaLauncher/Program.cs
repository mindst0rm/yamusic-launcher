using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Spectre.Console;
using YaLauncher.Application;
using YaLauncher.Services;
using YaLauncher.Storage;
using YaLauncher.Utils;

namespace YaLauncher;

[SupportedOSPlatform("windows")]
internal static class Program
{
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

    static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.Title = $"YaMusic Launcher v{AppVersionProvider.DisplayVersion}";

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

    private static async Task<int> RunInteractiveMenuAsync(
        AppConfig cfg,
        string? startupTitle = null,
        string? startupBody = null,
        bool skipLauncherSelfUpdate = false)
    {
        var orchestrator = CreateOrchestrator();

        if (!skipLauncherSelfUpdate && await TryHandleLauncherSelfUpdateAsync(cfg, orchestrator))
            return 0;

        if (!string.IsNullOrWhiteSpace(startupBody))
        {
            RedrawHeader();
            ShowInfoPanel(startupTitle ?? "Информация", startupBody);
            PauseAndContinue();
            IntroAnimationShown = true;
        }

        while (true)
        {
            if (!IntroAnimationShown)
            {
                await ShowIntroAnimationAsync();
                IntroAnimationShown = true;
            }

            RedrawHeader();
            ShowLauncherStateSummary(cfg);
            if (!orchestrator.IsInitialSetupDone(cfg.InstallDir!))
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
                orchestrator = CreateOrchestrator();
                continue;
            }

            var action = PromptMenuAction(section, cfg, orchestrator);
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
                        await RunInitialSetupInteractiveAsync(cfg, orchestrator);
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
                        await InstallClientOnlyInteractiveAsync(cfg, orchestrator);
                        break;

                    case MenuAction.UpdateMod:
                        if (!EnsureElevatedIfRequired(cfg.InstallDir!, [ArgRunUpdateMod], EmptyFlags))
                            return 0;
                        await UpdateModOnlyInteractiveAsync(cfg, orchestrator);
                        break;

                    case MenuAction.ShowLatestModVersion:
                        await ShowLatestModVersionInteractiveAsync(cfg);
                        break;

                    case MenuAction.PatchClient:
                        if (!EnsureElevatedIfRequired(cfg.InstallDir!, [ArgRunPatch], EmptyFlags))
                            return 0;
                        PatchOnlyInteractive(cfg, orchestrator);
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
                        CreateShortcutsInteractive(cfg, orchestrator);
                        break;

                    case MenuAction.LaunchViaLauncher:
                        if (!EnsureElevatedIfRequired(cfg.InstallDir!, [ArgBootstrap, ArgLaunchClient], EmptyFlags))
                            return 0;
                        await RunBootstrapInteractiveAsync(cfg, orchestrator);
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
            orchestrator = CreateOrchestrator();
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
                    "[1] Основные действия",
                    "[2] Установка и обновление",
                    "[3] Полезные утилиты",
                    "[4] Настройки",
                    "[0] Выход"
                }));

        return selected switch
        {
            "[1] Основные действия" => MenuSection.Core,
            "[2] Установка и обновление" => MenuSection.InstallUpdate,
            "[3] Полезные утилиты" => MenuSection.Utilities,
            "[4] Настройки" => MenuSection.Settings,
            _ => MenuSection.Exit
        };
    }

    private static MenuAction? PromptMenuAction(MenuSection section, AppConfig cfg, LauncherOrchestrator orchestrator)
    {
        var hasInstalledClientInSelectedDir = orchestrator.IsInitialSetupDone(cfg.InstallDir!);
        var initialSetupChoice = hasInstalledClientInSelectedDir
            ? "[grey][1] Первичная установка (уже выполнена)[/]"
            : "[1] Первичная установка (1/4 клиент -> 2/4 мод -> 3/4 патч -> 4/4 ярлыки)";

        var choices = section switch
        {
            MenuSection.Core => new[]
            {
                initialSetupChoice,
                "[2] Запустить Я.Музыку через лаунчер",
                "[0] Назад"
            },
            MenuSection.InstallUpdate => new[]
            {
                "[1] Переустановить клиент Я.Музыки",
                "[2] Обновить мод (app.asar)",
                "[3] Пропатчить установленный клиент",
                "[4] Создать/обновить ярлыки",
                "[0] Назад"
            },
            MenuSection.Utilities => new[]
            {
                "[1] Показать версии мода и changelog (GitHub)",
                "[2] Восстановить мод из бэкапа",
                "[3] Удалить бэкапы",
                "[0] Назад"
            },
            _ => ["[0] Назад"]
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
            "[2] Запустить Я.Музыку через лаунчер" => MenuAction.LaunchViaLauncher,
            "[1] Переустановить клиент Я.Музыки" => MenuAction.InstallClient,
            "[2] Обновить мод (app.asar)" => MenuAction.UpdateMod,
            "[3] Пропатчить установленный клиент" => MenuAction.PatchClient,
            "[4] Создать/обновить ярлыки" => MenuAction.CreateShortcuts,
            "[1] Показать версии мода и changelog (GitHub)" => MenuAction.ShowLatestModVersion,
            "[2] Восстановить мод из бэкапа" => MenuAction.RestoreBackup,
            "[3] Удалить бэкапы" => MenuAction.DeleteBackups,
            _ => null
        };
    }

    private static async Task<int> RunInitialSetupCommandAsync(AppConfig cfg, IReadOnlySet<string> flags)
    {
        if (!EnsureElevatedIfRequired(cfg.InstallDir!, [ArgRunInitialSetup], flags))
            return 0;

        var result = await CreateOrchestrator().ExecuteInitialSetupAsync(cfg, ParallelDownloads, PrintSetupStage);
        RedrawHeader();
        ShowInitialSetupResult(result);
        return 0;
    }

    private static async Task<int> RunInstallClientCommandAsync(AppConfig cfg, IReadOnlySet<string> flags)
    {
        if (!EnsureElevatedIfRequired(cfg.InstallDir!, [ArgRunInstallClient], flags))
            return 0;

        var exe = await CreateOrchestrator().InstallClientAsync(cfg, ParallelDownloads);
        RedrawHeader();
        ShowInfoPanel("Клиент установлен", $"Каталог: {cfg.InstallDir}\nEXE: {exe}");
        return 0;
    }

    private static async Task<int> RunUpdateModCommandAsync(AppConfig cfg, IReadOnlySet<string> flags)
    {
        if (!EnsureElevatedIfRequired(cfg.InstallDir!, [ArgRunUpdateMod], flags))
            return 0;

        var result = await CreateOrchestrator().UpdateModAsync(cfg);
        RedrawHeader();
        ShowModUpdateResult(result);
        return 0;
    }

    private static int RunPatchCommand(AppConfig cfg, IReadOnlySet<string> flags)
    {
        if (!EnsureElevatedIfRequired(cfg.InstallDir!, [ArgRunPatch], flags))
            return 0;

        var orchestrator = CreateOrchestrator();
        var patched = orchestrator.PatchClient(cfg);
        var exe = new InstalledClientLocator().FindInstalledExeOrThrow(cfg.InstallDir!);

        RedrawHeader();
        ShowInfoPanel("Патч применен", $"EXE: {exe}\nОтключено участков: {patched}");
        return 0;
    }

    private static int RunCreateShortcutsCommand(AppConfig cfg, IReadOnlySet<string> flags)
    {
        if (!EnsureElevatedIfRequired(cfg.InstallDir!, [ArgRunCreateShortcuts], flags))
            return 0;

        var shortcuts = CreateOrchestrator().CreateShortcuts(cfg);
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
        var orchestrator = CreateOrchestrator();

        RedrawHeader();

        var readiness = orchestrator.GetBootstrapReadiness(cfg);
        if (readiness.Status != BootstrapReadinessStatus.Ready)
        {
            var body = readiness.Status == BootstrapReadinessStatus.ClientMissingAfterSetup
                ? "Клиент Яндекс Музыки больше не найден в каталоге установки.\n" +
                  "Похоже, он был удален вручную или очищен вместе с папкой установки.\n\n" +
                  "Нужно заново выполнить чистую установку через пункт 'Первичная установка' " +
                  "или 'Переустановить клиент Я.Музыки'."
                : "Первичная установка не выполнена.\n" +
                  "Запустите лаунчер в интерактивном режиме и выполните шаги установки.";

            ShowInfoPanel("Запуск через ярлык недоступен", body);
            PauseAndContinue();
            IntroAnimationShown = true;
            return await RunInteractiveMenuAsync(cfg, skipLauncherSelfUpdate: true);
        }

        if (!EnsureElevatedIfRequired(cfg.InstallDir!, rawArgs, flags))
            return 0;

        var launchClient = flags.Contains(ArgLaunchClient);
        var noUpdate = flags.Contains(ArgNoUpdate);
        var owner = string.IsNullOrWhiteSpace(cfg.GitHubOwner) ? AppConfig.DefaultGitHubOwner : cfg.GitHubOwner;
        var repo = string.IsNullOrWhiteSpace(cfg.GitHubRepo) ? AppConfig.DefaultGitHubRepo : cfg.GitHubRepo;

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

        if (await TryHandleLauncherSelfUpdateAsync(cfg, orchestrator, WriteBootstrapLog))
            return 0;

        var result = await orchestrator.ExecuteBootstrapAsync(cfg, launchClient, noUpdate, WriteBootstrapLog);
        if (!string.IsNullOrWhiteSpace(result.Warning))
            WriteBootstrapLog(result.Warning, "yellow");

        WriteBootstrapLog("Последовательность запуска завершена.", "green");

        return 0;
    }

    private static async Task RunInitialSetupInteractiveAsync(AppConfig cfg, LauncherOrchestrator orchestrator)
    {
        var result = await orchestrator.ExecuteInitialSetupAsync(cfg, ParallelDownloads, PrintSetupStage);
        RedrawHeader();
        ShowInitialSetupResult(result);
    }

    private static async Task InstallClientOnlyInteractiveAsync(AppConfig cfg, LauncherOrchestrator orchestrator)
    {
        var exe = await orchestrator.InstallClientAsync(cfg, ParallelDownloads);
        RedrawHeader();
        ShowInfoPanel("Клиент переустановлен", $"Каталог: {cfg.InstallDir}\nEXE: {exe}");
    }

    private static async Task UpdateModOnlyInteractiveAsync(AppConfig cfg, LauncherOrchestrator orchestrator)
    {
        var result = await orchestrator.UpdateModAsync(cfg);
        RedrawHeader();
        ShowModUpdateResult(result);
    }

    private static async Task ShowLatestModVersionInteractiveAsync(AppConfig cfg)
    {
        var updater = new ModClientUpdater();
        var owner = string.IsNullOrWhiteSpace(cfg.GitHubOwner) ? AppConfig.DefaultGitHubOwner : cfg.GitHubOwner;
        var repo = string.IsNullOrWhiteSpace(cfg.GitHubRepo) ? AppConfig.DefaultGitHubRepo : cfg.GitHubRepo;
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

    private static void PatchOnlyInteractive(AppConfig cfg, LauncherOrchestrator orchestrator)
    {
        var patched = orchestrator.PatchClient(cfg);
        var exe = new InstalledClientLocator().FindInstalledExeOrThrow(cfg.InstallDir!);

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

        var choices = backups.Select((backup, index) => $"{index + 1}) {backup.FileName} ({backup.CreatedAt:yyyy-MM-dd HH:mm:ss})").ToList();
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
        new ProcessControllerAdapter().StopAllAsync().GetAwaiter().GetResult();
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
                    "[1] Удалить один бэкап",
                    "[2] Очистить все бэкапы",
                    "[0] Назад"
                }));

        if (mode == "[0] Назад")
            return;

        if (mode == "[2] Очистить все бэкапы")
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
            .Select((backup, index) =>
            {
                long size = 0;
                try { size = new FileInfo(backup.FullPath).Length; } catch { }
                return $"{index + 1}) {backup.FileName} ({backup.CreatedAt:yyyy-MM-dd HH:mm:ss}, {FormatBytes(size)})";
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

    private static void CreateShortcutsInteractive(AppConfig cfg, LauncherOrchestrator orchestrator)
    {
        var shortcuts = orchestrator.CreateShortcuts(cfg);
        RedrawHeader();
        ShowInfoPanel("Ярлыки обновлены",
            $"Я.Музыка (Desktop): {shortcuts.MusicDesktopShortcutPath}\n" +
            $"Я.Музыка (Start Menu): {shortcuts.MusicStartMenuShortcutPath}\n" +
            $"Лаунчер (Desktop): {shortcuts.LauncherDesktopShortcutPath}\n" +
            $"Лаунчер (Start Menu): {shortcuts.LauncherStartMenuShortcutPath}");
    }

    private static async Task RunBootstrapInteractiveAsync(AppConfig cfg, LauncherOrchestrator orchestrator)
    {
        if (!orchestrator.IsInitialSetupDone(cfg.InstallDir!))
            throw new InvalidOperationException("Первичная установка не завершена.");

        var result = await orchestrator.ExecuteBootstrapAsync(cfg, launchClient: true, noUpdate: false);

        RedrawHeader();
        ShowInfoPanel("Запуск через лаунчер выполнен",
            $"EXE: {result.ExePath}\nПатч участков: {result.PatchedCount}" +
            (string.IsNullOrWhiteSpace(result.Warning) ? string.Empty : $"\nПредупреждение: {result.Warning}"));
    }

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
            .Where(line => !string.IsNullOrWhiteSpace(line))
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
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
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

            var launcherUpdateState = cfg.AutoUpdateLauncher ? "включено" : "выключено";
            var modUpdateState = cfg.AutoUpdateBeforeLaunch ? "включено" : "выключено";
            var setupDone = CreateOrchestrator().IsInitialSetupDone(cfg.InstallDir!);
            var backupsLimitText = cfg.BackupAutoCleanupLimitMb <= 0
                ? "выключена"
                : $"{cfg.BackupAutoCleanupLimitMb} МБ";
            AnsiConsole.MarkupLine($"[grey]Каталог установки:[/] {Markup.Escape(cfg.InstallDir ?? "-")}");
            AnsiConsole.MarkupLine($"[grey]Auto-update лаунчера:[/] {launcherUpdateState}");
            AnsiConsole.MarkupLine($"[grey]Auto-update мода перед запуском:[/] {modUpdateState}");
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
                        "[1] Изменить путь установки",
                        "[2] Сбросить путь к стандартному",
                        "[3] Переключить auto-update лаунчера",
                        "[4] Переключить auto-update мода перед запуском",
                        "[5] Лимит авто-очистки бэкапов (МБ)",
                        "[0] Назад"
                    }));

            if (choice == "[0] Назад")
                return;

            if (choice == "[1] Изменить путь установки")
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

            if (choice == "[2] Сбросить путь к стандартному")
            {
                cfg.InstallDir = GetDefaultInstallDir();
                AppConfigStore.Save(cfg);
                continue;
            }

            if (choice == "[3] Переключить auto-update лаунчера")
            {
                cfg.AutoUpdateLauncher = !cfg.AutoUpdateLauncher;
                AppConfigStore.Save(cfg);
                continue;
            }

            if (choice == "[4] Переключить auto-update мода перед запуском")
            {
                cfg.AutoUpdateBeforeLaunch = !cfg.AutoUpdateBeforeLaunch;
                AppConfigStore.Save(cfg);
                continue;
            }

            if (choice == "[5] Лимит авто-очистки бэкапов (МБ)")
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
        AnsiConsole.MarkupLine($"[bold blue]by m1ndst0rm v{Markup.Escape(AppVersionProvider.DisplayVersion)}[/]");
        AnsiConsole.Write(new Rule("[grey]────────────────────────────────────────[/]").RuleStyle("grey").LeftJustified());
        AnsiConsole.WriteLine();
    }

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

    private static async Task<bool> TryHandleLauncherSelfUpdateAsync(
        AppConfig cfg,
        LauncherOrchestrator orchestrator,
        Action<string, string>? log = null)
    {
        if (!cfg.AutoUpdateLauncher)
        {
            log?.Invoke("Автообновление лаунчера отключено в настройках.", "grey");
            return false;
        }

        log?.Invoke("Проверяем обновление самого лаунчера...", "cyan");
        var result = await orchestrator.TrySelfUpdateLauncherAsync();

        switch (result.Status)
        {
            case LauncherSelfUpdateStatus.UpToDate:
                log?.Invoke($"Лаунчер уже актуален ({result.CurrentVersion}).", "green");
                return false;

            case LauncherSelfUpdateStatus.UpdateStarted:
                if (log is not null)
                {
                    log($"Найдена новая версия лаунчера: {result.CurrentVersion} -> {result.LatestVersion}", "green");
                    log("Запущен установщик обновления. После завершения установки повторите запуск ярлыка.", "yellow");
                }
                else
                {
                    RedrawHeader();
                    ShowInfoPanel(
                        "Обновление лаунчера",
                        $"Текущая версия: {result.CurrentVersion}\n" +
                        $"Доступна версия: {result.LatestVersion}\n" +
                        "Запущен установщик новой версии.\n" +
                        "После завершения установки откройте лаунчер снова.");
                }

                return true;

            case LauncherSelfUpdateStatus.NoInstallerAsset:
            case LauncherSelfUpdateStatus.Failed:
                log?.Invoke(result.Message ?? "Проверка обновления лаунчера завершилась с предупреждением.", "yellow");
                return false;

            default:
                return false;
        }
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
            .StartAsync("Инициализация меню и модулей...", async _ => { await Task.Delay(450); });
    }

    private static void ShowLauncherStateSummary(AppConfig cfg)
    {
        var setupDone = CreateOrchestrator().IsInitialSetupDone(cfg.InstallDir!);
        var statusColor = setupDone ? "green" : "yellow";
        var statusText = setupDone ? "готово" : "требуется";
        var launcherAutoUpdate = cfg.AutoUpdateLauncher ? "вкл" : "выкл";
        var modAutoUpdate = cfg.AutoUpdateBeforeLaunch ? "вкл" : "выкл";
        var backupLimit = cfg.BackupAutoCleanupLimitMb <= 0
            ? "выкл"
            : $"{cfg.BackupAutoCleanupLimitMb} МБ";

        var body =
            $"[grey]Установка:[/] [white]{Markup.Escape(cfg.InstallDir ?? "-")}[/]\n" +
            $"[grey]Первичная настройка:[/] [{statusColor}]{statusText}[/]\n" +
            $"[grey]Auto-update лаунчера:[/] [white]{launcherAutoUpdate}[/]\n" +
            $"[grey]Auto-update мода:[/] [white]{modAutoUpdate}[/]\n" +
            $"[grey]Лимит бэкапов:[/] [white]{backupLimit}[/]\n" +
            $"[grey]GitHub мод:[/] [white]{Markup.Escape(cfg.GitHubOwner)}/{Markup.Escape(cfg.GitHubRepo)}[/]";

        var panel = new Panel(new Markup(body))
            .Header("[bold]Состояние лаунчера[/]")
            .Border(BoxBorder.Rounded)
            .Expand();
        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
    }

    private static LauncherOrchestrator CreateOrchestrator()
    {
        var paths = LauncherPaths.CreateDefault();
        var locator = new InstalledClientLocator();

        return new LauncherOrchestrator(
            new LauncherPrerequisites(paths),
            new ProcessControllerAdapter(),
            locator,
            new SpectreClientInstallationService(paths),
            new SpectreModInstallationService(),
            new FusePatchService(),
            new ShortcutProvisioner(paths, locator),
            new AppConfigPersistence(),
            new ClientLauncher(),
            new LauncherSelfUpdater());
    }
}
