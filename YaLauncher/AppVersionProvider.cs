using System.Reflection;

namespace YaLauncher;

internal static class AppVersionProvider
{
    public static string DisplayVersion
    {
        get
        {
            var assembly = typeof(AppVersionProvider).Assembly;
            var informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;

            if (!string.IsNullOrWhiteSpace(informational))
                return informational.Split('+', 2)[0];

            return assembly.GetName().Version?.ToString() ?? "unknown";
        }
    }
}
