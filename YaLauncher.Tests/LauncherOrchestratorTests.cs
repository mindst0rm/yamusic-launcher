using YaLauncher.Application;
using YaLauncher.Services;
using YaLauncher.Storage;

namespace YaLauncher.Tests;

public sealed class LauncherOrchestratorTests
{
    [Fact]
    public async Task ExecuteInitialSetupAsync_RunsExpectedStepsAndSavesConfig()
    {
        var calls = new List<string>();
        var cfg = CreateConfig();
        var orchestrator = CreateOrchestrator(
            calls,
            clientInstallResult: "C:\\Music\\Yandex Music.exe",
            modResult: new ModInstallResult(true, "v1", "v2"),
            patchResult: 3);

        var result = await orchestrator.ExecuteInitialSetupAsync(cfg, 6, stage => calls.Add($"stage:{stage}"));

        Assert.Equal("C:\\Music\\Yandex Music.exe", result.ExePath);
        Assert.Equal(3, result.PatchedCount);
        Assert.True(cfg.IsInitialSetupCompleted);
        Assert.Equal(
            [
                "ensure7zip",
                "validate:C:\\Music",
                "stop",
                "stage:Шаг 1/4: скачивание и установка клиента Я.Музыки",
                "install-client:1/4",
                "stage:Шаг 2/4: скачивание и установка модифицированного app.asar",
                "install-mod:2/4",
                "stage:Шаг 3/4: патчинг клиента через AsarFusePatcher.dll",
                "patch:C:\\Music\\Yandex Music.exe",
                "stage:Шаг 4/4: создание ярлыков",
                "shortcuts:C:\\Music\\Yandex Music.exe",
                "save"
            ],
            calls);
    }

    [Fact]
    public async Task ExecuteBootstrapAsync_SkipsUpdateAndPatchWhenAutoUpdateDisabled()
    {
        using var temp = new TemporaryDirectory();
        var calls = new List<string>();
        var cfg = CreateConfig(temp.Path);
        cfg.AutoUpdateBeforeLaunch = false;
        var orchestrator = CreateOrchestrator(calls);

        var result = await orchestrator.ExecuteBootstrapAsync(cfg, launchClient: false, noUpdate: false);

        Assert.Null(result.ModResult);
        Assert.Equal(0, result.PatchedCount);
        Assert.DoesNotContain(calls, call => call.StartsWith("install-mod", StringComparison.Ordinal));
        Assert.DoesNotContain(calls, call => call.StartsWith("patch:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteBootstrapAsync_PatchesAndLaunchesWhenModUpdated()
    {
        using var temp = new TemporaryDirectory();
        var calls = new List<string>();
        var cfg = CreateConfig(temp.Path);
        var orchestrator = CreateOrchestrator(
            calls,
            modResult: new ModInstallResult(true, "v1", "v2"),
            patchResult: 5);

        var result = await orchestrator.ExecuteBootstrapAsync(cfg, launchClient: true, noUpdate: false);

        Assert.Equal(5, result.PatchedCount);
        Assert.Contains(calls, call => call.StartsWith("install-mod:", StringComparison.Ordinal));
        Assert.Contains("patch:C:\\Music\\Yandex Music.exe", calls);
        Assert.Contains("launch:C:\\Music\\Yandex Music.exe", calls);
    }

    [Fact]
    public async Task UpdateModAsync_StopsProcessesBeforeUpdating()
    {
        var calls = new List<string>();
        var cfg = CreateConfig();
        var orchestrator = CreateOrchestrator(calls, modResult: new ModInstallResult(false, "v1", "v1"));

        await orchestrator.UpdateModAsync(cfg);

        Assert.Equal(["stop", "install-mod:"], calls);
    }

    [Fact]
    public void GetBootstrapReadiness_ReturnsMissingClientAfterSetup_WhenClientWasRemoved()
    {
        var calls = new List<string>();
        var cfg = CreateConfig();
        cfg.IsInitialSetupCompleted = true;
        var orchestrator = CreateOrchestrator(calls, isInitialSetupDone: false);

        var readiness = orchestrator.GetBootstrapReadiness(cfg);

        Assert.Equal(BootstrapReadinessStatus.ClientMissingAfterSetup, readiness.Status);
    }

    [Fact]
    public async Task TrySelfUpdateLauncherAsync_UsesInjectedUpdater()
    {
        var calls = new List<string>();
        var orchestrator = CreateOrchestrator(
            calls,
            launcherUpdateResult: new LauncherSelfUpdateResult(
                LauncherSelfUpdateStatus.UpdateStarted,
                "1.1.6",
                "1.1.7",
                "https://github.com/mindst0rm/yamusic-launcher/releases/tag/v1.1.7",
                "C:\\Temp\\YaMusicLauncher-Setup-1.1.7.exe",
                "ok"));

        var result = await orchestrator.TrySelfUpdateLauncherAsync();

        Assert.True(result.UpdateStarted);
        Assert.Contains("self-update:1.1.6", calls);
    }

    [Fact]
    public void PatchClientAndCreateShortcuts_UseInjectedDependencies()
    {
        var calls = new List<string>();
        var cfg = CreateConfig();
        var orchestrator = CreateOrchestrator(calls, patchResult: 7);

        var patched = orchestrator.PatchClient(cfg);
        var shortcuts = orchestrator.CreateShortcuts(cfg);

        Assert.Equal(7, patched);
        Assert.Equal("desktop-music", shortcuts.MusicDesktopShortcutPath);
        Assert.Contains("locate-installed", calls);
        Assert.Contains("patch:C:\\Music\\Yandex Music.exe", calls);
        Assert.Contains(calls, call => call.StartsWith("shortcuts:", StringComparison.Ordinal));
    }

    private static AppConfig CreateConfig(string installDir = "C:\\Music") => new()
    {
        InstallDir = installDir,
        AutoUpdateLauncher = true,
        AutoUpdateBeforeLaunch = true,
        GitHubOwner = "owner",
        GitHubRepo = "repo",
        BackupAutoCleanupLimitMb = 300
    };

    private static LauncherOrchestrator CreateOrchestrator(
        List<string> calls,
        string clientInstallResult = "C:\\Music\\Yandex Music.exe",
        ModInstallResult? modResult = null,
        int patchResult = 0,
        bool isInitialSetupDone = true,
        LauncherSelfUpdateResult? launcherUpdateResult = null)
    {
        return new LauncherOrchestrator(
            new FakePrerequisites(calls),
            new FakeProcessController(calls),
            new FakeClientLocator(calls, isInitialSetupDone),
            new FakeClientInstallationService(calls, clientInstallResult),
            new FakeModInstallationService(calls, modResult ?? new ModInstallResult(false, "v1", "v1")),
            new FakePatchService(calls, patchResult),
            new FakeShortcutProvisioner(calls),
            new FakeConfigPersistence(calls),
            new FakeClientLauncher(calls),
            new FakeLauncherSelfUpdateService(
                calls,
                launcherUpdateResult ?? new LauncherSelfUpdateResult(
                    LauncherSelfUpdateStatus.UpToDate,
                    "1.1.6",
                    "1.1.6",
                    null,
                    null,
                    null)));
    }

    private sealed class FakePrerequisites : ILauncherPrerequisites
    {
        private readonly List<string> _calls;

        public FakePrerequisites(List<string> calls) => _calls = calls;

        public void EnsureSevenZipAvailable() => _calls.Add("ensure7zip");
        public void ValidateInstallPathForDelete(string installDir) => _calls.Add($"validate:{installDir}");
    }

    private sealed class FakeProcessController : IProcessController
    {
        private readonly List<string> _calls;

        public FakeProcessController(List<string> calls) => _calls = calls;

        public Task StopAllAsync(CancellationToken ct = default)
        {
            _calls.Add("stop");
            return Task.CompletedTask;
        }
    }

    private sealed class FakeClientLocator : IInstalledClientLocator
    {
        private readonly List<string> _calls;
        private readonly bool _isInitialSetupDone;

        public FakeClientLocator(List<string> calls, bool isInitialSetupDone)
        {
            _calls = calls;
            _isInitialSetupDone = isInitialSetupDone;
        }

        public string FindInstalledExeOrThrow(string installDir)
        {
            _calls.Add("locate-installed");
            return "C:\\Music\\Yandex Music.exe";
        }

        public bool IsInitialSetupDone(string installDir) => _isInitialSetupDone;
        public string? TryFindInstalledExe(string installDir) => _isInitialSetupDone ? "C:\\Music\\Yandex Music.exe" : null;
    }

    private sealed class FakeClientInstallationService : IClientInstallationService
    {
        private readonly List<string> _calls;
        private readonly string _result;

        public FakeClientInstallationService(List<string> calls, string result)
        {
            _calls = calls;
            _result = result;
        }

        public Task<string> InstallLatestClientAsync(string installDir, int parallel, string stagePrefix = "", CancellationToken ct = default)
        {
            _calls.Add($"install-client:{stagePrefix}");
            return Task.FromResult(_result);
        }
    }

    private sealed class FakeModInstallationService : IModInstallationService
    {
        private readonly List<string> _calls;
        private readonly ModInstallResult _result;

        public FakeModInstallationService(List<string> calls, ModInstallResult result)
        {
            _calls = calls;
            _result = result;
        }

        public Task<ModInstallResult> InstallLatestModAsync(AppConfig cfg, string installDir, string stagePrefix = "", CancellationToken ct = default)
        {
            _calls.Add($"install-mod:{stagePrefix}");
            return Task.FromResult(_result);
        }
    }

    private sealed class FakePatchService : IPatchService
    {
        private readonly List<string> _calls;
        private readonly int _result;

        public FakePatchService(List<string> calls, int result)
        {
            _calls = calls;
            _result = result;
        }

        public int ApplyPatchOrThrow(string exePath)
        {
            _calls.Add($"patch:{exePath}");
            return _result;
        }
    }

    private sealed class FakeShortcutProvisioner : IShortcutProvisioner
    {
        private readonly List<string> _calls;

        public FakeShortcutProvisioner(List<string> calls) => _calls = calls;

        public ShortcutResult CreateOrUpdateShortcuts(AppConfig cfg, string? iconPath = null)
        {
            _calls.Add($"shortcuts:{iconPath ?? string.Empty}");
            return new ShortcutResult("desktop-music", "start-music", "desktop-launcher", "start-launcher");
        }
    }

    private sealed class FakeConfigPersistence : IConfigPersistence
    {
        private readonly List<string> _calls;

        public FakeConfigPersistence(List<string> calls) => _calls = calls;

        public void Save(AppConfig cfg) => _calls.Add("save");
    }

    private sealed class FakeClientLauncher : IClientLauncher
    {
        private readonly List<string> _calls;

        public FakeClientLauncher(List<string> calls) => _calls = calls;

        public void Launch(string exePath) => _calls.Add($"launch:{exePath}");
    }

    private sealed class FakeLauncherSelfUpdateService : ILauncherSelfUpdateService
    {
        private readonly List<string> _calls;
        private readonly LauncherSelfUpdateResult _result;

        public FakeLauncherSelfUpdateService(List<string> calls, LauncherSelfUpdateResult result)
        {
            _calls = calls;
            _result = result;
        }

        public Task<LauncherSelfUpdateResult> TrySelfUpdateAsync(string currentVersion, CancellationToken ct = default)
        {
            _calls.Add($"self-update:{currentVersion}");
            return Task.FromResult(_result);
        }
    }
}
