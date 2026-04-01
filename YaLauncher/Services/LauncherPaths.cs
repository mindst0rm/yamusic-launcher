namespace YaLauncher.Services;

internal sealed class LauncherPaths
{
    public LauncherPaths(string workDir, string sevenZipExePath, string launcherExePath, string launcherIconPath)
    {
        WorkDir = workDir;
        SevenZipExePath = sevenZipExePath;
        LauncherExePath = launcherExePath;
        LauncherIconPath = launcherIconPath;
    }

    public string WorkDir { get; }
    public string SevenZipExePath { get; }
    public string LauncherExePath { get; }
    public string LauncherIconPath { get; }

    public static LauncherPaths CreateDefault()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var launcherExePath = Environment.ProcessPath
                              ?? throw new InvalidOperationException("Не удалось определить путь текущего EXE.");

        return new LauncherPaths(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "YaMusicLauncher",
                "work"),
            Path.Combine(baseDirectory, "7zip", "7za.exe"),
            launcherExePath,
            Path.Combine(baseDirectory, "assets", "launcher.ico"));
    }
}
