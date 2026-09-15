using System.Text.Json.Serialization;

namespace OverClient.Core.Manifest;

/// <summary>
/// 根索引：很少变动，可以被 CDN / 客户端长时间缓存。
/// 它只负责回答"有哪些游戏、每个游戏的清单在哪儿、清单的哈希是多少"。
/// </summary>
public sealed class RootIndex
{
    public const int CurrentSchema = 1;

    public int SchemaVersion { get; set; } = CurrentSchema;
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<GameEntry> Games { get; set; } = [];
}

public sealed class GameEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Summary { get; set; }
    public string? CoverUrl { get; set; }
    public string? AccentColor { get; set; }
    public List<string> Tags { get; set; } = [];

    /// <summary>通道名（latest / beta / ...）→ 该通道当前版本的清单指针。</summary>
    public Dictionary<string, ChannelEntry> Channels { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public ChannelEntry? Channel(string name) =>
        Channels.TryGetValue(name, out var entry) ? entry : null;

    public ChannelEntry? DefaultChannel() => Channel("latest") ?? Channels.Values.FirstOrDefault();

    /// <summary>
    /// 按用户设置里的通道取指针，该通道不存在时退回默认通道。
    /// 卡片和安装流程必须用同一个方法取，否则会出现"卡片显示 beta、实际装的是 latest"。
    /// </summary>
    public ChannelEntry? ChannelFor(string? channel) =>
        string.IsNullOrWhiteSpace(channel) ? DefaultChannel() : Channel(channel) ?? DefaultChannel();
}

public sealed class ChannelEntry
{
    public string Version { get; set; } = "";
    public string ManifestUrl { get; set; } = "";

    /// <summary>清单自身的 sha256。清单是"带内"信任根，必须先校验哈希再解析。</summary>
    public string? ManifestSha256 { get; set; }

    public long ManifestSize { get; set; }
}

/// <summary>
/// 单个游戏单个版本的清单：内容寻址、不可变、可永久缓存。
/// 启动器的一切安装决策都从它推导，绝不硬编码游戏信息。
/// </summary>
public sealed class GameManifest
{
    public const int CurrentSchema = 1;

    public int SchemaVersion { get; set; } = CurrentSchema;
    public string GameId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Channel { get; set; } = "latest";
    public DateTimeOffset PublishedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? ReleaseNotes { get; set; }

    /// <summary>低于这个启动器版本必须拒绝安装并提示升级，而不是猜着解析。</summary>
    public string LauncherMinVersion { get; set; } = "0.0.0";

    /// <summary>文件 URL 的前缀；单个文件可用 Url 覆盖。</summary>
    public string BaseUrl { get; set; } = "";

    public List<FileEntry> Files { get; set; } = [];
    public LaunchSpec Launch { get; set; } = new();

    /// <summary>可选的 manifest 签名（ECDsa P-256，DER 编码）。demo 阶段允许为空。</summary>
    public ManifestSignature? Signature { get; set; }

    /// <summary>
    /// 这份清单 JSON 文件自身的 sha256。由 <see cref="ManifestService"/> 在校验通过后填上。
    ///
    /// 刻意不参与序列化：它描述的是文件本身的哈希，写回文件里既无意义也会自我指涉。
    /// 用途是判断"版本号没变但内容变了" —— 发布者忘记改版本号是常态，
    /// 只看版本号会让这类更新永远发不出去。
    /// </summary>
    [JsonIgnore]
    public string? ManifestSha256 { get; set; }

    public long TotalBytes => Files.Sum(f => f.Size);

    public FileEntry? Find(string path) =>
        Files.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
}

public sealed class FileEntry
{
    /// <summary>相对路径，POSIX 分隔符。绝不允许绝对路径或 ".."。</summary>
    public string Path { get; set; } = "";

    public long Size { get; set; }
    public string Sha256 { get; set; } = "";

    /// <summary>留空则用 manifest.BaseUrl + Path 拼接。</summary>
    public string? Url { get; set; }

    public bool Executable { get; set; }

    public string ResolveUrl(string baseUrl)
    {
        if (!string.IsNullOrWhiteSpace(Url))
            return Url!;

        var prefix = baseUrl.TrimEnd('/');
        var path = Path.Replace('\\', '/').TrimStart('/');
        return $"{prefix}/{path}";
    }
}

public sealed class LaunchSpec
{
    public string Executable { get; set; } = "";
    public List<string> Arguments { get; set; } = [];
    public string WorkingDirectory { get; set; } = ".";
}

public sealed class ManifestSignature
{
    public string Algorithm { get; set; } = "ECDSA-P256-SHA256";
    public string KeyId { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>
/// 站点根目录下的 <c>latest.json</c>：描述"当前发布的启动器版本"。
///
/// 为什么不复用游戏的 manifest：启动器自己不是游戏，不走安装器那条
/// （staging → 原子切换 → 硬链接复用）的路，它要替换的是**正在运行的自己**。
/// 所以它是站点根下一份独立的小 JSON，缺失时只表示"这个内容源不提供启动器更新"，
/// 不是错误 —— 老站点没有这个文件照样能正常用。
/// </summary>
public sealed class LauncherRelease
{
    public const int CurrentSchema = 1;

    public int SchemaVersion { get; set; } = CurrentSchema;

    /// <summary>最新启动器版本号，例如 0.2.0。</summary>
    public string Version { get; set; } = "";

    /// <summary>当前版本低于它就必须更新。留空表示只提示、不强制。</summary>
    public string? MinVersion { get; set; }

    /// <summary>新启动器本体的下载地址（通常指向站点里的 client\0verClient.exe）。</summary>
    public string? Url { get; set; }

    /// <summary>下载后必须校验通过才会被启用。为空则拒绝自动更新。</summary>
    public string? Sha256 { get; set; }

    public long Size { get; set; }

    public string? Notes { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }
}
