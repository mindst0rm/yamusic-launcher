# AGENTS.md

## Scope
These instructions apply to the whole repository.

## Project Snapshot
- `YaLauncher/` - .NET 8 CLI app (main entry: `YaLauncher/Program.cs`).
- `DLL_patching/` - C++ `AsarFusePatcher` native library and CLI samples (CMake project).
- `YaLauncher/7zip/` - bundled 7-Zip binaries used for extraction.
- `images_screen/` - README illustrations.

## What This App Does
- Downloads latest Yandex Music desktop build from Yandex S3 (`latest.yml` -> `path`).
- Extracts the downloaded installer via `7za.exe`.
- Locates Yandex Music executable.
- Calls native `AsarFusePatcher.dll` through P/Invoke (`YaLauncher/Native/FuseLib.cs`) to disable ASAR integrity fuse checks.

## Build And Run
- .NET SDK: 8.x (`YaLauncher/global.json` pins `8.0.0` with `latestMinor` roll-forward).
- Build launcher:
  - `dotnet build YaLauncher/YaLauncher.sln`
- Run launcher from project dir:
  - `dotnet run --project YaLauncher/YaLauncher.csproj`
- Build native patcher:
  - `cmake -S DLL_patching -B DLL_patching/build`
  - `cmake --build DLL_patching/build --config Release`

## Important Coupling
- `YaLauncher/YaLauncher.csproj` expects native DLL at:
  - `DLL_patching/cmake-build-release/AsarFusePatcher.dll`
- If native output path changes, update `AsarFuseNativePath` in `YaLauncher/YaLauncher.csproj`.

## Editing Guidance
- Keep compatibility with Windows (`SupportedOSPlatform("windows")`).
- Preserve CLI UX built with `Spectre.Console`.
- Do not remove bundled `7zip` artifacts unless task explicitly asks for packaging changes.
- For risky filesystem operations, preserve current safeguards (`SafeDeleteDirectory` logic and install path checks).

## Library Research Policy (Required)
- If work requires any external library/framework API details (new dependency, upgrade, unfamiliar API), use MCP server **Context7** first.
- Mandatory flow:
  - `resolve-library-id`
  - `query-docs`
- Prefer Context7 docs over memory for package behavior, signatures, and version-specific changes.

