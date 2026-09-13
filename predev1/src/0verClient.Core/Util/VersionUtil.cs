namespace OverClient.Core.Util;

public static class VersionUtil
{
    /// <summary>语义化版本比较；解析失败时退化为序数比较，绝不抛异常。</summary>
    public static int Compare(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) && string.IsNullOrWhiteSpace(b)) return 0;
        if (string.IsNullOrWhiteSpace(a)) return -1;
        if (string.IsNullOrWhiteSpace(b)) return 1;

        var left = a.Split('-', '+')[0];
        var right = b.Split('-', '+')[0];

        if (Version.TryParse(left, out var va) && Version.TryParse(right, out var vb))
            return va.CompareTo(vb);

        return string.CompareOrdinal(a, b);
    }

    public static bool IsAtLeast(string? actual, string? required) => Compare(actual, required) >= 0;
}
