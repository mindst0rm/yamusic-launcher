# CLAUDE.md

## Repository Context
- Project: YaMusic launcher + ASAR fuse patching toolkit.
- Primary app: `YaLauncher/` (.NET 8, CLI, `Spectre.Console` UI).
- Native component: `DLL_patching/` (CMake C++, exports `DisableAsarIntegrityFuse` for P/Invoke).

## Key Files
- `YaLauncher/Program.cs` - interactive flows: download+patch, reinstall+patch.
- `YaLauncher/Services/YandexMusicDownloader.cs` - resolves latest Yandex release and downloads it.
- `YaLauncher/Services/SevenZipExtractor.cs` - extraction via bundled `7za.exe`.
- `YaLauncher/Native/FuseLib.cs` - native library loading and error mapping.
- `YaLauncher/YaLauncher.csproj` - build/publish config and native DLL copy targets.

## Build Commands
- `dotnet build YaLauncher/YaLauncher.sln`
- `dotnet run --project YaLauncher/YaLauncher.csproj`
- `cmake -S DLL_patching -B DLL_patching/build`
- `cmake --build DLL_patching/build --config Release`

## Implementation Notes
- Runtime target is Windows x64.
- `YaLauncher` copies native artifacts during build/publish; keep this pipeline intact when refactoring.
- Do not break fallback paths in `FuseLib` (base dir / process dir / env override / runtimes folder).

## Required Documentation Source For Libraries
- If you need information about any external library/framework (usage, API, migration, version behavior), use MCP server **Context7**.
- Required sequence:
  1. `resolve-library-id`
  2. `query-docs`
- Treat Context7 output as the authoritative source before implementing dependency-related changes.

