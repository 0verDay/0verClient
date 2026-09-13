namespace OverClient.Core.Util;

/// <summary>启动器自身的信息。</summary>
public static class AppInfo
{
    public const string Name = "0verClient";
    public const string Version = "0.1.0";
    public const string UserAgent = "0verClient/0.1.0";
}

/// <summary>
/// 启动器所有落盘位置的唯一来源。
/// 设置 OVERCLIENT_HOME 可把整个启动器变成绿色便携版（全部数据放同一个目录）。
/// </summary>
public static class AppPaths
{
    public static string Root { get; } = ResolveRoot();

    public static string Games { get; } = Ensure("games");
    public static string State { get; } = Ensure("state");
    public static string Cache { get; } = Ensure("cache");
    public static string Logs { get; } = Ensure("logs");
    public static string Temp { get; } = Ensure("tmp");

    private static string ResolveRoot()
    {
        var overridden = Environment.GetEnvironmentVariable("OVERCLIENT_HOME");
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            var full = Path.GetFullPath(overridden);
            Directory.CreateDirectory(full);
            return full;
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = Path.Combine(local, AppInfo.Name);
        Directory.CreateDirectory(root);
        return root;
    }

    private static string Ensure(string child)
    {
        var path = Path.Combine(Root, child);
        Directory.CreateDirectory(path);
        return path;
    }
}
