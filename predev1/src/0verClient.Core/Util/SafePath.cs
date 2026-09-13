using System.Text;

namespace OverClient.Core.Util;

/// <summary>
/// manifest 里的一切路径都是"不可信输入"。
/// 这里的每个方法都假定 manifest 可能被投毒，必须在解压/落盘之前把路径钉死在游戏根目录内。
/// </summary>
public static class SafePath
{
    private static readonly char[] Separators = ['/', '\\'];

    /// <summary>把 manifest 里的相对路径规范化成 POSIX 风格，并拒绝一切可疑形态。</summary>
    public static string NormalizeRelative(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative))
            throw new InvalidDataException("manifest 中存在空路径");

        if (relative.Contains('\0'))
            throw new InvalidDataException($"路径包含 NUL 字符: {Escape(relative)}");

        var cleaned = relative.Replace('\\', '/').Trim();

        if (cleaned.StartsWith('/') || Path.IsPathRooted(cleaned) || cleaned.Length >= 2 && cleaned[1] == ':')
            throw new InvalidDataException($"manifest 不允许绝对路径: {Escape(relative)}");

        if (cleaned.StartsWith("//", StringComparison.Ordinal))
            throw new InvalidDataException($"manifest 不允许 UNC 路径: {Escape(relative)}");

        var segments = cleaned.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            throw new InvalidDataException($"路径规范化后为空: {Escape(relative)}");

        foreach (var segment in segments)
        {
            if (segment is "." or "..")
                throw new InvalidDataException($"manifest 不允许相对跳转 (路径穿越): {Escape(relative)}");

            if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidDataException($"路径包含非法文件名字符: {Escape(relative)}");

            if (segment.EndsWith(' ') || segment.EndsWith('.'))
                throw new InvalidDataException($"Windows 不允许以空格或点结尾的路径段: {Escape(relative)}");

            if (IsReservedDeviceName(segment))
                throw new InvalidDataException($"路径命中 Windows 保留设备名: {Escape(relative)}");
        }

        return string.Join('/', segments);
    }

    /// <summary>把相对路径解析成绝对路径，并做最终的双重防线校验（规范化后必须仍在 root 之内）。</summary>
    public static string Resolve(string root, string relative)
    {
        var normalized = NormalizeRelative(relative);
        var rootFull = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var combined = Path.GetFullPath(Path.Combine(rootFull, normalized.Replace('/', Path.DirectorySeparatorChar)));

        var guard = rootFull + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(guard, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"路径逃出了安装目录: {Escape(relative)}");

        return combined;
    }

    /// <summary>把 manifest 路径转成磁盘相对路径（用于创建目录）。</summary>
    public static string ToDiskRelative(string relative) =>
        NormalizeRelative(relative).Replace('/', Path.DirectorySeparatorChar);

    /// <summary>把来自网络的 id 变成一个安全的单层目录名（绝不接受分隔符与保留名）。</summary>
    public static string SanitizeSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException("gameId 为空");

        var safe = new string([.. value.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.')]);
        if (string.IsNullOrEmpty(safe) || safe is "." or ".." || IsReservedDeviceName(safe))
            throw new InvalidDataException($"非法 gameId: {value}");

        return safe;
    }

    /// <summary>比较两个路径是否指向同一位置（Windows 大小写不敏感）。</summary>
    public static bool IsSamePath(string a, string b) =>
        string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsReservedDeviceName(string segment)
    {
        var name = segment;
        var dot = name.IndexOf('.');
        if (dot >= 0) name = name[..dot];

        return name.ToUpperInvariant() switch
        {
            "CON" or "PRN" or "AUX" or "NUL" => true,
            "COM1" or "COM2" or "COM3" or "COM4" or "COM5" or "COM6" or "COM7" or "COM8" or "COM9" => true,
            "LPT1" or "LPT2" or "LPT3" or "LPT4" or "LPT5" or "LPT6" or "LPT7" or "LPT8" or "LPT9" => true,
            _ => false
        };
    }

    private static string Escape(string value) => value.Replace("\0", "\\0");

    public static string Describe(IEnumerable<string> segments)
    {
        var sb = new StringBuilder();
        foreach (var s in segments) sb.Append(s).Append(' ');
        return sb.ToString().TrimEnd();
    }
}
