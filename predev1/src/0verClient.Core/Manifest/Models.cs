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
