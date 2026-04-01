# YaMusic Launcher

[![Release](https://img.shields.io/github/v/release/mindst0rm/yamusic-launcher?display_name=tag)](https://github.com/mindst0rm/yamusic-launcher/releases)
[![Windows](https://img.shields.io/badge/platform-Windows%20x64-0078D6)](https://github.com/mindst0rm/yamusic-launcher)
[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)](https://dotnet.microsoft.com/)
[![Build](https://img.shields.io/badge/build-Release-success)](https://github.com/mindst0rm/yamusic-launcher/releases)
[![Packages](https://img.shields.io/badge/Packages-GitHub%20Packages-181717?logo=github)](https://github.com/mindst0rm?tab=packages)

Консольный лаунчер для Windows, который автоматизирует установку и запуск модифицированного клиента Яндекс Музыки.

## 🚀 Что делает лаунчер

Pipeline запуска и первичной установки:
1. Скачивает и устанавливает клиент Я.Музыки.
2. Скачивает мод (`app.asar`) из GitHub Releases.
3. Применяет патч через `AsarFusePatcher.dll`.
4. Создает ярлыки на музыку и на сам лаунчер.

При запуске через ярлык `Yandex Music Mod` выполняется bootstrap-последовательность:
- проверка новой версии самого лаунчера на GitHub Releases;
- проверка актуальной версии мода на GitHub;
- обновление (если нужно);
- патчинг;
- запуск клиента;
- вывод шапки и лога действий в консоли.

## ✨ Ключевые возможности

- Первичная установка в один сценарий (`1/4 -> 4/4`).
- Настройка каталога установки клиента.
- Автообновление самого лаунчера через GitHub Release installer.
- Автообновление мода перед запуском.
- Бэкапы `app.asar` с восстановлением и ручной очисткой.
- Автоочистка бэкапов по лимиту размера (настраивается в настройках, по умолчанию `300 MB`).
- Разделение меню на категории: основные действия, установка/обновление, утилиты, настройки.
- Создание ярлыков:
  - `Yandex Music Mod.lnk` - запуск через bootstrap;
  - `YaMusic Launcher.lnk` - запуск главного меню.

## ⚡ Быстрый старт

1. Открой `Releases`:  
   `https://github.com/mindst0rm/yamusic-launcher/releases`
2. Скачай `YaMusicLauncher-Setup-<version>.exe` (рекомендуется).
3. Установи лаунчер.
4. Запусти `YaMusic Launcher` и выбери `Первичная установка`.

Если при запуске через ярлык клиент Яндекс Музыки был удален вручную, лаунчер откроет главное меню с сообщением о необходимости заново выполнить чистую установку.

## 🧩 Установка и запуск готового EXE

### ✅ Вариант 1 (рекомендуется): через установщик Inno Setup

1. Скачай из `Releases` файл `YaMusicLauncher-Setup-<version>.exe`.
2. Запусти установщик и следуй шагам мастера.
3. После установки используй ярлыки:
   - `YaMusic Launcher` - открыть меню лаунчера;
   - `Yandex Music Mod` - запуск клиента через bootstrap (проверка обновления -> патчинг -> запуск).
4. При первом запуске из меню выполни `Первичная установка`.

### 📦 Вариант 2: portable EXE (без установки)

1. Возьми `YaLauncher.exe` из publish-артефактов (`artifacts/publish/win-x64`) или собери проект по инструкции ниже.
2. Убедись, что рядом находятся необходимые файлы:
   - `AsarFusePatcher.dll`
   - папка `7zip/`
3. Запусти `YaLauncher.exe`.
4. В меню выбери каталог установки клиента и выполни `Первичная установка`.

### 🔄 Обновление до новой версии лаунчера

1. Скачай новый `Setup` из `Releases`.
2. Установи поверх текущей версии.
3. В обычном режиме лаунчер также умеет сам скачать и запустить новый `Setup`, если в GitHub Releases появилась более свежая версия.
4. Конфиг и настройки сохраняются в `%AppData%\YaMusicLauncher\config.json`.

## 📦 Packages и дистрибуция

- Основной канал распространения: `GitHub Releases` (готовый Inno Setup installer).
- Файл релиза: `YaMusicLauncher-Setup-<version>.exe`.
- Источник версии в репозитории: корневой файл `VERSION`.
- Раздел `Packages` в GitHub заполняется публикацией NuGet-пакета `YaMusicLauncher.Distribution` через workflow.
- Источник пакетов: `https://nuget.pkg.github.com/mindst0rm/index.json`.
- Пакет содержит готовый publish-вывод лаунчера (`tools/win-x64`), включая `YaLauncher.exe`, `AsarFusePatcher.dll` и `7zip`.

Подключение GitHub Packages feed (если нужен доступ к пакету локально):

```powershell
dotnet nuget add source "https://nuget.pkg.github.com/mindst0rm/index.json" `
  --name "github-mindst0rm" `
  --username "<github-username>" `
  --password "<github-pat-with-read:packages>" `
  --store-password-in-clear-text
```

## 🤝 Credits

- Текущий источник релизов мода: [`mindst0rm/ModYandexClient`](https://github.com/mindst0rm/ModYandexClient).

## 🏗️ Архитектура проекта

### `YaLauncher/` (.NET 8)
- `Program.cs` - UI, CLI, меню, elevation и рендеринг через `Spectre.Console`.
- `Application/LauncherOrchestrator.cs` - orchestration high-level сценариев (`InitialSetup`, `Bootstrap`, install/update/patch/shortcuts).
- `Storage/AppConfigStore.cs` - загрузка/сохранение конфигурации.
- `Services/YandexMusicDownloader.cs` - загрузка клиента.
- `Services/ModClientUpdater.cs` - обновление мода, бэкапы, changelog.
- `Services/ShortcutService.cs` - создание ярлыков.
- `Services/YandexProcessManager.cs` - остановка процессов клиента.
- `Native/FuseLib.cs` - P/Invoke для `AsarFusePatcher.dll`.

### `DLL_patching/` (C++/CMake)
- Нативный патчер `AsarFusePatcher.dll`.
- Экспорт `DisableAsarIntegrityFuse`.

### `installer/`
- `YaMusicLauncher.iss` - Inno Setup script.

### `scripts/`
- `build-release.ps1` - release publish + сборка установщика + (опционально) публикация GitHub Release.

### `.github/`
- `release-notes-template.md` - единый шаблон красивого текста для GitHub Release.

## 🛠️ Сборка из исходников

Требования:
- Windows x64
- .NET SDK 8.x
- CMake + C++ toolchain
- Inno Setup 6 (для сборки инсталлятора)

Обычная сборка:

```powershell
dotnet build YaLauncher/YaLauncher.sln
```

Запуск тестов:

```powershell
dotnet test YaLauncher.Tests/YaLauncher.Tests.csproj
```

Релизная публикация:

```powershell
dotnet publish YaLauncher/YaLauncher.csproj -c Release -r win-x64 --self-contained true
```

Полная релизная сборка (publish + installer):

```powershell
$version = (Get-Content VERSION -Raw).Trim()
powershell -ExecutionPolicy Bypass -File .\scripts\build-release.ps1 -Version $version
```

Полная релизная сборка + автоматическая публикация GitHub Release по шаблону:

```powershell
$version = (Get-Content VERSION -Raw).Trim()
powershell -ExecutionPolicy Bypass -File .\scripts\build-release.ps1 -Version $version -PublishGitHubRelease
```

Только publish (без installer):

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-release.ps1 -SkipInstaller
```

## 🧾 Конфиги, логи, бэкапы

- Конфиг: `%AppData%\YaMusicLauncher\config.json`
- Лог мода: `<installDir>\resources\logs\ym_mod_manager.log`
- Бэкапы: `<installDir>\resources\backups_app`

## ⚠️ Дисклеймер

Проект не аффилирован с Яндексом. Использование - на ваш риск.
