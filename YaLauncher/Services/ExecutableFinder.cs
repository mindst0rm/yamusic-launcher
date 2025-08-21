namespace YaLauncher.Services;

internal static class ExecutableFinder
{
    private static readonly string[] CandidateNames =
    {
        "Яндекс Музыка.exe", "Yandex Music.exe", "YandexMusic.exe", "Yandex.Music.exe"
    };

    public static string FindExe(string root)
    {
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);

        // проходим все .exe и матчим по имени (без регистра)
        foreach (var file in Directory.EnumerateFiles(root, "*.exe", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (CandidateNames.Any(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase)))
                return file;
        }
        throw new FileNotFoundException("Не удалось найти исполняемый файл Яндекс Музыки в распакованной директории.", root);
    }
}