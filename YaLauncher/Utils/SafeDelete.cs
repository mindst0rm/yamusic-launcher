namespace YaLauncher.Utils;

internal static class SafeDelete
{
    public static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        for (int i = 0; i < 5; i++)
        {
            try { Directory.Delete(path, recursive: true); return; }
            catch { Thread.Sleep(120); }
        }
        // последняя попытка — чистим атрибуты и удаляем
        foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            Try(() => File.SetAttributes(f, FileAttributes.Normal));
        Directory.Delete(path, true);
    }

    private static void Try(Action a) { try { a(); } catch { } }
}