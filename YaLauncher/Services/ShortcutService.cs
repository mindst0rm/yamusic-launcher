using System.Runtime.Versioning;
using System.Runtime.InteropServices;

namespace YaLauncher.Services;

[SupportedOSPlatform("windows")]
internal sealed class ShortcutService
{
    private const string MusicShortcutFileName = "Yandex Music Mod.lnk";
    private const string LauncherShortcutFileName = "YaMusic Launcher.lnk";

    public ShortcutResult CreateOrUpdate(string launcherExePath, string musicArguments, string? musicIconPath = null)
    {
        var desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var startMenuDir = Environment.GetFolderPath(Environment.SpecialFolder.Programs);

        var musicDesktopShortcut = Path.Combine(desktopDir, MusicShortcutFileName);
        var musicStartMenuShortcut = Path.Combine(startMenuDir, MusicShortcutFileName);
        var launcherDesktopShortcut = Path.Combine(desktopDir, LauncherShortcutFileName);
        var launcherStartMenuShortcut = Path.Combine(startMenuDir, LauncherShortcutFileName);
        var launcherDir = Path.GetDirectoryName(launcherExePath)!;

        CreateShortcut(
            musicDesktopShortcut,
            launcherExePath,
            musicArguments,
            launcherDir,
            "Запуск Яндекс Музыки через мод-лаунчер",
            musicIconPath);

        CreateShortcut(
            musicStartMenuShortcut,
            launcherExePath,
            musicArguments,
            launcherDir,
            "Запуск Яндекс Музыки через мод-лаунчер",
            musicIconPath);

        CreateShortcut(
            launcherDesktopShortcut,
            launcherExePath,
            string.Empty,
            launcherDir,
            "Открыть меню YaMusic Launcher",
            launcherExePath);

        CreateShortcut(
            launcherStartMenuShortcut,
            launcherExePath,
            string.Empty,
            launcherDir,
            "Открыть меню YaMusic Launcher",
            launcherExePath);

        return new ShortcutResult(
            musicDesktopShortcut,
            musicStartMenuShortcut,
            launcherDesktopShortcut,
            launcherStartMenuShortcut);
    }

    private static void CreateShortcut(
        string shortcutPath,
        string targetPath,
        string arguments,
        string workingDirectory,
        string description,
        string? iconPath)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
                        ?? throw new InvalidOperationException("WScript.Shell is unavailable.");

        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(shellType)
                    ?? throw new InvalidOperationException("Failed to initialize WScript.Shell.");

            shortcut = shellType.InvokeMember(
                "CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod,
                binder: null,
                target: shell,
                args: [shortcutPath]);

            if (shortcut == null)
                throw new InvalidOperationException("Failed to create shortcut COM object.");

            var shortcutType = shortcut.GetType();
            shortcutType.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, shortcut, [targetPath]);
            shortcutType.InvokeMember("Arguments", System.Reflection.BindingFlags.SetProperty, null, shortcut, [arguments]);
            shortcutType.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, shortcut, [workingDirectory]);
            shortcutType.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, shortcut, [description]);

            var iconValue = string.IsNullOrWhiteSpace(iconPath) ? targetPath : iconPath;
            shortcutType.InvokeMember("IconLocation", System.Reflection.BindingFlags.SetProperty, null, shortcut, [$"{iconValue},0"]);
            shortcutType.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);
        }
        finally
        {
            if (shortcut != null && Marshal.IsComObject(shortcut))
                Marshal.ReleaseComObject(shortcut);

            if (shell != null && Marshal.IsComObject(shell))
                Marshal.ReleaseComObject(shell);
        }
    }
}

internal sealed record ShortcutResult(
    string MusicDesktopShortcutPath,
    string MusicStartMenuShortcutPath,
    string LauncherDesktopShortcutPath,
    string LauncherStartMenuShortcutPath);
