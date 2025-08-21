using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;

namespace YaLauncher.Native
{
    internal static class FuseLib
    {
        // Коды ошибок из AsarFusePatcher.h
        public const int E_ARGS = -1;  // неверные аргументы/путь
        public const int E_IO   = -2;  // ошибка ввода-вывода
        public const int E_PE   = -3;  // ошибка парсинга PE
        public const int E_FAIL = -4;  // прочая ошибка

        // Сигнатура экспорта: int WINAPI DisableAsarIntegrityFuse(wchar_t* exePath, BOOL dryRun, int limit, wchar_t* errBuf, int errBufChars)
        [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
        private delegate int DisableAsarIntegrityFuseDelegate(
            string exePath,
            [MarshalAs(UnmanagedType.Bool)] bool dryRun,
            int limit,
            StringBuilder errBuf,
            int errBufChars);

        private static readonly DisableAsarIntegrityFuseDelegate _disable;

        static FuseLib()
        {
            // загружаем нативку и биндим делегат
            var lib = LoadNativeOrThrow(out var tried, out var lastErr);
            try
            {
                var p = NativeLibrary.GetExport(lib, "DisableAsarIntegrityFuse");
                _disable = Marshal.GetDelegateForFunctionPointer<DisableAsarIntegrityFuseDelegate>(p);
            }
            catch (Exception ex)
            {
                var msg = new StringBuilder()
                    .AppendLine("Не удалось получить экспорт DisableAsarIntegrityFuse из AsarFusePatcher.")
                    .AppendLine($"Последняя ошибка: {ex.GetType().Name}: {ex.Message}")
                    .AppendLine("Пробованные пути для загрузки DLL:")
                    .AppendLine(" - " + string.Join("\n - ", tried))
                    .ToString();
                throw new EntryPointNotFoundException(msg, lastErr ?? ex);
            }
        }

        private static nint LoadNativeOrThrow(out List<string> tried, out Exception? lastErr)
        {
            tried = new List<string>();
            lastErr = null;

            string baseDir = AppContext.BaseDirectory;
            string procDir = Path.GetDirectoryName(Environment.ProcessPath!) ?? baseDir;
            string asmDir  = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? baseDir;
            string cwd     = Environment.CurrentDirectory;

            // Разрешим переопределить путь через переменную окружения (удобно для CI/portable)
            var overridePath = Environment.GetEnvironmentVariable("ASARFUSE_DLL");

            // Имена библиотек под разные сборки (MinGW может класть libAsarFusePatcher.dll)
            var winNames = new[] { "AsarFusePatcher.dll", "libAsarFusePatcher.dll" };
            var linuxNames = new[] { "libAsarFusePatcher.so" };
            var osxNames = new[] { "libAsarFusePatcher.dylib" };

            // Список кандидатов
            var candidates = new List<string?>();

            if (!string.IsNullOrWhiteSpace(overridePath))
                candidates.Add(overridePath);

            // Загрузка по имени (пусть OS loader сам найдёт)
            candidates.Add(null); // "AsarFusePatcher" по имени

            // Явные пути
            foreach (var name in SelectNamesForPlatform(winNames, linuxNames, osxNames))
            {
                candidates.Add(Path.Combine(baseDir, name));
                candidates.Add(Path.Combine(procDir, name));
                candidates.Add(Path.Combine(asmDir,  name));
                candidates.Add(Path.Combine(cwd,     name));

                // стандартная папка runtimes/<rid>/native
                var rid = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win-x64"
                        : RuntimeInformation.IsOSPlatform(OSPlatform.Linux)   ? "linux-x64"
                        : "osx-x64";
                candidates.Add(Path.Combine(baseDir, "runtimes", rid, "native", name));
            }

            foreach (var p in candidates)
            {
                try
                {
                    nint h;
                    if (p is null)
                    {
                        // По имени: AsarFusePatcher (без расширения)
                        if (NativeLibrary.TryLoad("AsarFusePatcher", out h))
                            return h;
                        tried.Add("(name) AsarFusePatcher");
                    }
                    else
                    {
                        tried.Add(p);
                        if (NativeLibrary.TryLoad(p, out h))
                            return h;
                    }
                }
                catch (Exception ex)
                {
                    lastErr = ex;
                }
            }

            var message =
                "Не удалось загрузить нативную библиотеку AsarFusePatcher.\n" +
                "Пробованные пути:\n - " + string.Join("\n - ", tried);
            if (lastErr != null) message += $"\nПоследняя ошибка: {lastErr.GetType().Name}: {lastErr.Message}";
            throw new DllNotFoundException(message, lastErr);
        }

        private static IEnumerable<string> SelectNamesForPlatform(string[] win, string[] linux, string[] osx)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return win;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))   return linux;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))     return osx;
            // fallback
            return win;
        }

        // ---------- Публичные обёртки ----------

        /// <summary>Вызов нативной функции. Возвращает rc и текст ошибки из DLL.</summary>
        public static int Disable(string exePath, bool dryRun, int limit, out string error)
        {
            var sb = new StringBuilder(512);
            int rc = _disable(exePath, dryRun, limit, sb, sb.Capacity);
            error = sb.ToString();
            return rc;
        }

        /// <summary>Сканирование без изменений: сколько мест будет отключено.</summary>
        public static int DryRun(string exePath, out string error) =>
            Disable(exePath, dryRun: true, limit: -1, out error);

        /// <summary>Патч всех найденных мест (без лимита).</summary>
        public static int PatchAll(string exePath, out string error) =>
            Disable(exePath, dryRun: false, limit: -1, out error);

        /// <summary>Патч не более N мест.</summary>
        public static int PatchLimited(string exePath, int limit, out string error) =>
            Disable(exePath, dryRun: false, limit: limit, out error);
    }
}
