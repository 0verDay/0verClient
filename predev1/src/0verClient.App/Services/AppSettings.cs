using System.IO;
using OverClient.Core;
using OverClient.Core.Manifest;
using OverClient.Core.Util;

namespace OverClient.App.Services;

/// <summary>启动器设置。刻意只有一个文件、纯 JSON、无依赖。</summary>
public sealed class AppSettings
{
    /// <summary>内容源索引地址。默认指向本地 demo 源。</summary>
    public string IndexUrl { get; set; } = "http://127.0.0.1:8787/index.json";

    public string Channel { get; set; } = "latest";

    public bool KillGamesOnExit { get; set; }

    /// <summary>
    /// 调试用：放行非回环的明文 http 内容源。
    /// 打开它还不够 —— 主机必须同时出现在 <see cref="AllowedHosts"/> 里，
    /// 这样"图省事"不会变成一个能把所有主机都放开的总开关。
    /// </summary>
    public bool AllowInsecureHttp { get; set; }

    /// <summary>允许的主机，逗号/分号/空白分隔。留空表示不限制 https 主机。</summary>
    public string AllowedHosts { get; set; } = "";

    public static string FilePath => System.IO.Path.Combine(AppPaths.Root, "settings.json");

    /// <summary>
    /// 随程序一起分发的预设文件（放在 exe 旁边）。
    /// 用途：把客户端发给玩家时，不用让每个人手打服务器地址 —— 预设里写好即可。
    /// 规则：只有用户还没有自己的 settings.json 时，预设才作为初始值生效。
    /// </summary>
    public static string PresetFilePath => System.IO.Path.Combine(AppContext.BaseDirectory, "launcher.json");

    public IReadOnlyList<string> ParseAllowedHosts() =>
        AllowedHosts.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public UrlPolicy BuildUrlPolicy() => new(ParseAllowedHosts(), AllowInsecureHttp);

    public string DescribePolicy()
    {
        var hosts = ParseAllowedHosts();
        var hostText = hosts.Count == 0 ? "不限主机" : string.Join(", ", hosts);
        return AllowInsecureHttp ? $"http 已放行 · 白名单 {hostText}" : $"仅 https · 白名单 {hostText}";
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonDefaults.Deserialize<AppSettings>(File.ReadAllBytes(FilePath));
                Log.Info($"设置加载完成，索引地址 {loaded.IndexUrl}（{loaded.DescribePolicy()}）");
                return loaded;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"设置读取失败，使用默认值：{ex.Message}");
        }

        // 没有用户设置时，用随 exe 分发的预设当初始值。
        try
        {
            if (File.Exists(PresetFilePath))
            {
                var preset = JsonDefaults.Deserialize<AppSettings>(File.ReadAllBytes(PresetFilePath));
                Log.Info($"使用随程序分发的预设 {PresetFilePath}：索引 {preset.IndexUrl}（{preset.DescribePolicy()}）");
                return preset;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"launcher.json 预设读取失败，改用内置默认值：{ex.Message}");
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            System.IO.File.WriteAllText(FilePath, JsonDefaults.Serialize(this));
            Log.Info($"设置已保存到 {FilePath}（{DescribePolicy()}）");
        }
        catch (Exception ex)
        {
            Log.Error("设置保存失败", ex);
        }
    }
}
