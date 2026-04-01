using YaLauncher.Services;
using YaLauncher.Storage;

namespace YaLauncher.Application;

internal sealed record InitialSetupResult(
    string InstallDir,
    string ExePath,
    ModInstallResult ModResult,
    int PatchedCount,
    ShortcutResult Shortcuts);

internal sealed record BootstrapResult(
    string ExePath,
    ModInstallResult? ModResult,
    int PatchedCount,
    string? Warning);

internal enum BootstrapReadinessStatus
{
    Ready,
    InitialSetupRequired,
    ClientMissingAfterSetup
}

internal sealed record BootstrapReadiness(BootstrapReadinessStatus Status);

internal enum LauncherSelfUpdateStatus
{
    UpToDate,
    UpdateStarted,
    NoInstallerAsset,
    Failed
}

internal sealed record LauncherSelfUpdateResult(
    LauncherSelfUpdateStatus Status,
    string CurrentVersion,
    string? LatestVersion,
    string? ReleaseUrl,
    string? InstallerPath,
    string? Message)
{
    public bool UpdateStarted => Status == LauncherSelfUpdateStatus.UpdateStarted;
}

internal interface ILauncherPrerequisites
{
    void EnsureSevenZipAvailable();
    void ValidateInstallPathForDelete(string installDir);
}

internal interface IProcessController
{
    Task StopAllAsync(CancellationToken ct = default);
}

internal interface IInstalledClientLocator
{
    string FindInstalledExeOrThrow(string installDir);
    bool IsInitialSetupDone(string installDir);
    string? TryFindInstalledExe(string installDir);
}

internal interface IClientInstallationService
{
    Task<string> InstallLatestClientAsync(
        string installDir,
        int parallel,
        string stagePrefix = "",
        CancellationToken ct = default);
}

internal interface IModInstallationService
{
    Task<ModInstallResult> InstallLatestModAsync(
        AppConfig cfg,
        string installDir,
        string stagePrefix = "",
        CancellationToken ct = default);
}

internal interface IPatchService
{
    int ApplyPatchOrThrow(string exePath);
}

internal interface IShortcutProvisioner
{
    ShortcutResult CreateOrUpdateShortcuts(AppConfig cfg, string? iconPath = null);
}

internal interface IConfigPersistence
{
    void Save(AppConfig cfg);
}

internal interface IClientLauncher
{
    void Launch(string exePath);
}

internal interface ILauncherSelfUpdateService
{
    Task<LauncherSelfUpdateResult> TrySelfUpdateAsync(
        string currentVersion,
        Action<string, string>? log = null,
        CancellationToken ct = default);
}
