# YaMusic Launcher

Консольный лаунчер для установки и запуска модифицированного клиента Яндекс Музыки на Windows.

Лаунчер автоматизирует полный цикл:
1. скачивание клиента Я.Музыки;
2. скачивание и установка `app.asar` из GitHub-релиза мода;
3. патчинг EXE через `AsarFusePatcher.dll` (отключение ASAR integrity fuse);
4. создание ярлыков.

## Возможности

- Первичная установка в один сценарий (`1/4 -> 4/4`).
- Выбор каталога установки клиента.
- Обновление мода (`app.asar`) с версионированием (`app.version`) и бэкапами.
- Патчинг клиента через нативную DLL (`DLL_patching`).
- Запуск Я.Музыки через bootstrap-режим лаунчера с авто-проверкой обновлений.
- Создание ярлыков:
  - `Yandex Music Mod.lnk` (запуск музыки через лаунчер),
  - `YaMusic Launcher.lnk` (запуск меню лаунчера).
- Категоризированное меню и ASCII-анимации при старте интерфейса.

## Архитектура

### `YaLauncher/` (.NET 8)
- `Program.cs` - интерактивное меню, пайплайн установки, bootstrap-режим.
- `Services/YandexMusicDownloader.cs` - загрузка актуального клиента из `latest.yml`.
- `Services/ModClientUpdater.cs` - логика обновления/бэкапа/восстановления `app.asar`.
- `Services/ShortcutService.cs` - создание ярлыков через `WScript.Shell`.
- `Native/FuseLib.cs` - P/Invoke-обертка для `AsarFusePatcher.dll`.

### `DLL_patching/` (C++/CMake)
- Нативная библиотека `AsarFusePatcher.dll`.
- Экспорт `DisableAsarIntegrityFuse` для патчинга целевого EXE.

## Сборка

## Требования

- Windows x64
- .NET SDK 8.x
- CMake + C++ toolchain (MSVC/MinGW)
- 7-Zip (бинарники уже включены в `YaLauncher/7zip`)

### Debug/обычная сборка

```powershell
dotnet build YaLauncher/YaLauncher.sln
```

### Release publish (self-contained)

```powershell
dotnet publish YaLauncher/YaLauncher.csproj -c Release -r win-x64 --self-contained true
```

`YaLauncher.csproj` автоматически:
- собирает `AsarFusePatcher.dll` через CMake;
- копирует DLL (и при наличии runtime-зависимости MinGW) в build/publish output.

## Установщик (Setup.exe)

В проекте добавлены:
- Inno Setup script: `installer/YaMusicLauncher.iss`
- release script: `scripts/build-release.ps1`

Команда сборки релиза + установщика:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-release.ps1 -Version 1.1.0
```

Что делает скрипт:
1. `dotnet publish` в `artifacts/publish/win-x64`;
2. компиляция Inno Setup через `ISCC.exe`;
3. генерация `Setup.exe` в `installer/output`.

Если нужен только publish без инсталлятора:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-release.ps1 -SkipInstaller
```

## Использование

Запуск лаунчера:

```powershell
dotnet run --project YaLauncher/YaLauncher.csproj
```

В меню доступны разделы:
- `Основные действия`
- `Установка и обновление`
- `Полезные утилиты`
- `Настройки`

Рекомендуемый первый шаг: `Первичная установка`.

## Примечания

- Конфиг хранится в `%AppData%\YaMusicLauncher\config.json`.
- Логи обновления мода: `<installDir>\resources\logs\ym_mod_manager.log`.
- Бэкапы `app.asar`: `<installDir>\resources\backups_app`.

## Дисклеймер

Проект не аффилирован с Яндексом и предоставляется как есть.
