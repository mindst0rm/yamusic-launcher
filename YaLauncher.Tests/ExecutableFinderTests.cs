using YaLauncher.Services;

namespace YaLauncher.Tests;

public sealed class ExecutableFinderTests
{
    [Fact]
    public void FindExe_ReturnsMatchingCandidateFromNestedDirectory()
    {
        using var temp = new TemporaryDirectory();
        var nested = System.IO.Path.Combine(temp.Path, "app", "bin");
        Directory.CreateDirectory(nested);
        var exePath = System.IO.Path.Combine(nested, "Yandex Music.exe");
        File.WriteAllText(exePath, "stub");

        var result = ExecutableFinder.FindExe(temp.Path);

        Assert.Equal(exePath, result);
    }

    [Fact]
    public void FindExe_ThrowsWhenCandidateDoesNotExist()
    {
        using var temp = new TemporaryDirectory();

        var action = () => ExecutableFinder.FindExe(temp.Path);

        Assert.Throws<FileNotFoundException>(action);
    }
}
