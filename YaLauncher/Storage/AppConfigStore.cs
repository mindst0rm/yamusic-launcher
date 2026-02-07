using System.Text.Json;

namespace YaLauncher.Storage;

internal sealed class AppConfig
{
    public string? InstallDir { get; set; }
    public bool AutoUpdateBeforeLaunch { get; set; } = true;
    public string GitHubOwner { get; set; } = "TheKing-OfTime";
    public string GitHubRepo { get; set; } = "YandexMusicModClient";
    public int LaunchTimeoutSeconds { get; set; } = 30;
    public int BackupAutoCleanupLimitMb { get; set; } = 300;
    public bool IsInitialSetupCompleted { get; set; }
}

internal static class AppConfigStore
{
    private static readonly string DefaultInstallDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs",
        "YandexMusic");

    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "YaMusicLauncher",
        "config.json");

    public static AppConfig Load()
    {
        var cfg = new AppConfig();
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                cfg = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
            }
        }
        catch
        {
            // ignore
        }

        return Normalize(cfg);
    }

    public static void Save(AppConfig cfg)
    {
        var normalized = Normalize(cfg);
        var dir = Path.GetDirectoryName(ConfigPath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(normalized, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ConfigPath, json);
    }

    private static AppConfig Normalize(AppConfig cfg)
    {
        cfg.InstallDir = string.IsNullOrWhiteSpace(cfg.InstallDir)
            ? DefaultInstallDir
            : cfg.InstallDir.Trim();

        if (string.IsNullOrWhiteSpace(cfg.GitHubOwner))
            cfg.GitHubOwner = "TheKing-OfTime";

        if (string.IsNullOrWhiteSpace(cfg.GitHubRepo))
            cfg.GitHubRepo = "YandexMusicModClient";

        if (cfg.LaunchTimeoutSeconds <= 0)
            cfg.LaunchTimeoutSeconds = 30;

        if (cfg.BackupAutoCleanupLimitMb < 0)
            cfg.BackupAutoCleanupLimitMb = 300;

        return cfg;
    }
}
