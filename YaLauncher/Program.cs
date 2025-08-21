using System.Runtime.Versioning;
using Spectre.Console;
using YaLauncher.Native;
using YaLauncher.Utils;
using YaLauncher.Services;

namespace YaLauncher;

[SupportedOSPlatform("windows")]
internal static class Program
{
    private static readonly string WorkDir     = Path.Combine(AppContext.BaseDirectory, "work");
    private static readonly string SevenZipExe = Path.Combine(AppContext.BaseDirectory, "7zip", "7za.exe");

    static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.Title = "Asar Fuse Patcher";

        while (true)
        {
            RedrawHeader();

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold]Выберите действие[/]")
                    .PageSize(10)
                    .AddChoices(new[]
                    {
                        "⬇️ Скачать последний билд Яндекс.Музыки и пропатчить",
                        "🔁 Обновить текущую версию Я.Музыки и пропатчить",
                        "🚪 Выход"
                    }));

            switch (choice)
            {
                case "⬇️ Скачать последний билд Яндекс.Музыки и пропатчить":
                    await RunDownloadAndPatchAsync(parallel: 6);
                    PauseAndContinue();
                    break;

                case "🔁 Обновить текущую версию Я.Музыки и пропатчить":
                    await RunReinstallAndPatchAsync(parallel: 6);
                    PauseAndContinue();
                    break;

                case "🚪 Выход":
                    AnsiConsole.MarkupLine("[grey]До встречи![/]");
                    await Task.Delay(200);
                    return 0;
            }
        }
    }

    // =========================== сценарии ===========================

    private static async Task RunDownloadAndPatchAsync(int parallel)
    {
        EnsureSevenZip();

        string archivePath = string.Empty;
        string unpackDir   = Path.Combine(WorkDir, "unpacked");
        string locatedExe  = string.Empty;
        int    patched     = 0;
        string errText     = string.Empty;

        var downloader = new YandexMusicDownloader();

        await AnsiConsole.Progress()
            .Columns(new ProgressColumn[]
            {
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new DownloadedColumn(),        // custom ниже
                new TransferSpeedColumn(),     // custom ниже
                new RemainingTimeColumn()      // custom ниже
            })
            .StartAsync(async ctx =>
            {
                var tDownload = ctx.AddTask("[cyan]Загрузка релиза[/]", autoStart: true);
                var tExtract  = ctx.AddTask("[yellow]Распаковка[/]", maxValue: 100, autoStart: false);
                var tPatch    = ctx.AddTask("[green]Патчинг[/]", maxValue: 1, autoStart: false);

                // --- download ---
                var dlProgress = new Progress<DownloadProgress>(p =>
                {
                    if (tDownload.MaxValue == 100) // ещё не инициализировали реальный максимум
                        tDownload.MaxValue = Math.Max(1, p.TotalBytes);

                    tDownload.Value = p.ReceivedBytes;
                    tDownload.Description = $"[cyan]Загрузка релиза[/] ({FormatBytes(p.ReceivedBytes)}/{FormatBytes(p.TotalBytes)})";
                    ctx.Refresh();
                });

                (archivePath, _) = await downloader.DownloadLatestAsync(WorkDir, parallel, dlProgress);
                tDownload.Value = tDownload.MaxValue;

                // --- extract ---
                tExtract.StartTask();
                await SevenZipExtractor.ExtractAsync(SevenZipExe, archivePath, unpackDir,
                    progress: new Progress<double>(prc => { tExtract.Value = prc; }),
                    ct: default);
                tExtract.Value = tExtract.MaxValue;

                // --- locate exe ---
                locatedExe = LocateYandexExe(unpackDir)
                             ?? throw new FileNotFoundException("EXE не найден после распаковки.");

                // --- patch ---
                tPatch.StartTask();
                var dry = FuseLib.Disable(locatedExe, true, -1, out var _errDry);
                if (dry < 0) { errText = _errDry ?? string.Empty; patched = dry; return; }
                tPatch.MaxValue = Math.Max(1, dry);
                var rc = FuseLib.Disable(locatedExe, false, -1, out var err);
                if (rc < 0) { errText = err ?? string.Empty; patched = rc; return; }
                patched = rc;
                tPatch.Value = tPatch.MaxValue;
            });

        // итог
        RedrawHeader();
        if (patched < 0)
        {
            ShowError(patched, errText);
        }
        else
        {
            var panel = new Panel(
                    $"[green]Готово.[/]\n" +
                    $"[grey]EXE:[/] {Markup.Escape(locatedExe)}\n" +
                    $"[grey]Отключено участков:[/] [bold]{patched}[/]\n" +
                    $"[grey]Бэкап:[/] создан [italic].fuses.bak[/] (если отсутствовал)")
                .Header("[bold green]Патч применён[/]")
                .Border(BoxBorder.Rounded);
            AnsiConsole.Write(panel);
        }
    }

    private static async Task RunReinstallAndPatchAsync(int parallel)
    {
        EnsureSevenZip();

        // найдём текущую установку
        var installDir = DetectInstallDir()
                         ?? AskDirectory("Укажите [yellow]каталог установки Яндекс.Музыки[/]:");
        if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
        {
            ShowError(FuseLib.E_ARGS, "Каталог установки не найден.");
            return;
        }

        // очень простая защита от случайного удаления
        if (!installDir.Contains("YandexMusic", StringComparison.OrdinalIgnoreCase) &&
            !installDir.Contains("Яндекс", StringComparison.OrdinalIgnoreCase))
        {
            ShowError(FuseLib.E_ARGS, $"Каталог выглядит подозрительно: {installDir}");
            return;
        }

        string archivePath = string.Empty;
        string locatedExe  = string.Empty;
        int    patched     = 0;
        string errText     = string.Empty;

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
                var tCleanup  = ctx.AddTask("[grey]Удаление старой версии[/]", maxValue: 1, autoStart: true);
                var tDownload = ctx.AddTask("[cyan]Загрузка релиза[/]", autoStart: false);
                var tExtract  = ctx.AddTask("[yellow]Распаковка в каталог установки[/]", maxValue: 100, autoStart: false);
                var tPatch    = ctx.AddTask("[green]Патчинг[/]", maxValue: 1, autoStart: false);

                // --- cleanup ---
                SafeDeleteDirectory(installDir);
                tCleanup.Value = 1;

                // --- download ---
                tDownload.StartTask();
                var dlProgress = new Progress<DownloadProgress>(p =>
                {
                    if (tDownload.MaxValue == 100)
                        tDownload.MaxValue = Math.Max(1, p.TotalBytes);

                    tDownload.Value = p.ReceivedBytes;
                    tDownload.Description = $"[cyan]Загрузка релиза[/] ({FormatBytes(p.ReceivedBytes)}/{FormatBytes(p.TotalBytes)})";
                });
                (archivePath, _) = await downloader.DownloadLatestAsync(WorkDir, parallel, dlProgress);
                tDownload.Value = tDownload.MaxValue;

                // --- extract directly into installDir ---
                tExtract.StartTask();
                Directory.CreateDirectory(installDir);
                await SevenZipExtractor.ExtractAsync(SevenZipExe, archivePath, installDir,
                    progress: new Progress<double>(prc => tExtract.Value = prc), ct: default);
                tExtract.Value = tExtract.MaxValue;

                // --- locate exe ---
                locatedExe = LocateYandexExe(installDir)
                             ?? throw new FileNotFoundException("EXE не найден после распаковки.");

                // --- patch ---
                tPatch.StartTask();
                var dry = FuseLib.Disable(locatedExe, true, -1, out var _errDry);
                if (dry < 0) { errText = _errDry ?? string.Empty; patched = dry; return; }
                tPatch.MaxValue = Math.Max(1, dry);
                var rc = FuseLib.Disable(locatedExe, false, -1, out var err);
                if (rc < 0) { errText = err ?? string.Empty; patched = rc; return; }
                patched = rc;
                tPatch.Value = tPatch.MaxValue;
            });

        // итог
        RedrawHeader();
        if (patched < 0)
        {
            ShowError(patched, errText);
        }
        else
        {
            var panel = new Panel(
                    $"[green]Готово.[/]\n" +
                    $"[grey]Каталог:[/] {Markup.Escape(installDir)}\n" +
                    $"[grey]EXE:[/] {Markup.Escape(locatedExe)}\n" +
                    $"[grey]Отключено участков:[/] [bold]{patched}[/]")
                .Header("[bold green]Переустановлено и пропатчено[/]")
                .Border(BoxBorder.Rounded);
            AnsiConsole.Write(panel);
        }
    }

    // =========================== утилиты ===========================

    private static void EnsureSevenZip()
    {
        if (!File.Exists(SevenZipExe))
            throw new FileNotFoundException($"7za.exe не найден по пути: {SevenZipExe}");
    }

    private static string? LocateYandexExe(string root)
    {
        // частые варианты: «Яндекс Музыка.exe», «Yandex Music.exe»
        var names = new[] { "Яндекс Музыка.exe", "Yandex Music.exe" };
        foreach (var name in names)
        {
            var path = Directory.EnumerateFiles(root, name, SearchOption.AllDirectories).FirstOrDefault();
            if (path != null) return path;
        }
        // fallback: любой *.exe с «Music» в имени
        return Directory.EnumerateFiles(root, "*.exe", SearchOption.AllDirectories)
                        .FirstOrDefault(p => Path.GetFileName(p).Contains("Music", StringComparison.OrdinalIgnoreCase));
    }

    private static string? DetectInstallDir()
    {
        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var guess = Path.Combine(local, "Programs", "YandexMusic");
            if (Directory.Exists(guess)) return guess;

            // альтернативный вариант: в «Programs\Yandex\YandexMusic»
            var alt = Path.Combine(local, "Programs", "Yandex", "YandexMusic");
            if (Directory.Exists(alt)) return alt;
        }
        catch { /* ignore */ }
        return null;
    }

    private static void SafeDeleteDirectory(string dir)
    {
        // Ничего «вверх» и никаких системных корней
        if (string.IsNullOrWhiteSpace(dir)) throw new ArgumentException(nameof(dir));
        var full = Path.GetFullPath(dir);
        if (full.Length < 8) throw new InvalidOperationException($"Подозрительный путь: {full}");
        if (!Directory.Exists(full)) return;

        // пытаемся снять read-only у файлов
        foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
        {
            try
            {
                var attr = File.GetAttributes(file);
                if (attr.HasFlag(FileAttributes.ReadOnly))
                    File.SetAttributes(file, attr & ~FileAttributes.ReadOnly);
            } catch { /* ignore */ }
        }
        Directory.Delete(full, recursive: true);
    }

    private static string AskDirectory(string prompt)
    {
        return AnsiConsole.Prompt(
            new TextPrompt<string>(prompt)
                .PromptStyle("cyan")
                .Validate(path =>
                    string.IsNullOrWhiteSpace(path)
                        ? ValidationResult.Error("[red]Путь не может быть пустым[/]")
                        : Directory.Exists(path)
                            ? ValidationResult.Success()
                            : ValidationResult.Error("[red]Каталог не найден[/]")));
    }

    private static async Task RunWithStatus(string title, Func<Task> body)
    {
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("yellow"))
            .StartAsync(title, async _ => await body());
    }

    private static void PauseAndContinue()
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]Нажмите любую клавишу…[/]");
        Console.ReadKey(true);
    }

    private static void RedrawHeader()
    {
        Console.Clear();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(FiggleFonts.Slant.Render("YaMusic Launcher"));
        Console.ForegroundColor = ConsoleColor.Blue;
        Console.WriteLine(FiggleFonts.Slant.Render("by m1ndst0rm v1.0.0"));
        Console.ResetColor();
        Console.WriteLine(new string('-', 40));
        Console.WriteLine();
    }

    private static string FormatBytes(long v)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double x = v;
        int i = 0;
        while (x >= 1024 && i < units.Length - 1) { x /= 1024; i++; }
        return $"{x:0.##} {units[i]}";
    }
    
    private static void ShowError(int rc, string? err)
    {
        var reason = rc switch
        {
            FuseLib.E_ARGS => "Неверные аргументы/путь (E_ARGS)",
            FuseLib.E_IO   => "Ошибка ввода-вывода (E_IO)",
            FuseLib.E_PE   => "Ошибка парсинга PE (E_PE)",
            FuseLib.E_FAIL => "Неизвестная ошибка (E_FAIL)",
            _              => $"Код {rc}"
        };

        var panel = new Panel(
                $"[red]{Markup.Escape(reason)}[/]\n[grey]{Markup.Escape(err ?? string.Empty)}[/]")
            .Header("[bold red]Ошибка[/]")
            .Border(BoxBorder.Rounded);

        AnsiConsole.Write(panel);
    }
}
