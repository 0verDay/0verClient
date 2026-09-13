using System.Security.Cryptography;

namespace OverClient.Core.Util;

/// <summary>内容寻址的基础：sha256 是 manifest 与磁盘之间唯一的真理。</summary>
public static class Hashing
{
    public static async Task<string> Sha256FileAsync(string path, CancellationToken ct = default)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);

        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    public static string Sha256Bytes(ReadOnlySpan<byte> data) =>
        Convert.ToHexStringLower(SHA256.HashData(data));

    public static bool Equals(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public static string HumanBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.##} {units[unit]}";
    }

    public static string HumanSpeed(double bytesPerSecond) => HumanBytes((long)bytesPerSecond) + "/s";

    public static string HumanEta(TimeSpan eta) =>
        eta.TotalHours >= 1 ? $"{(int)eta.TotalHours} 小时 {eta.Minutes} 分"
        : eta.TotalMinutes >= 1 ? $"{(int)eta.TotalMinutes} 分 {eta.Seconds} 秒"
        : $"{Math.Max(0, (int)eta.TotalSeconds)} 秒";
}
