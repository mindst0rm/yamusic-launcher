namespace YaLauncher.Services;

public sealed record DownloadProgress(
    long TotalBytes,
    long ReceivedBytes,
    double SpeedBytesPerSec,
    TimeSpan? Eta);