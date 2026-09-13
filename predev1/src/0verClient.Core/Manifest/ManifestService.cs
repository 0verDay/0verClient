using OverClient.Core.Net;
using OverClient.Core.Util;

namespace OverClient.Core.Manifest;

/// <summary>
/// 把静态托管的索引/清单取回来并校验。
/// 无后端方案下这里是信任链的入口：清单哈希必须与索引里声明的一致。
/// </summary>
public sealed class ManifestService(IContentSource source, UrlPolicy policy)
{
    private readonly IContentSource _source = source;
    private readonly UrlPolicy _policy = policy;

    /// <summary>索引来自哪里。用来发现"索引在服务器、文件却指向本机"这种自相矛盾的清单。</summary>
    private Uri? _indexUri;

    public async Task<RootIndex> LoadIndexAsync(string indexUrl, CancellationToken ct = default)
    {
        _policy.AssertAllowed(indexUrl);
        _indexUri = new Uri(indexUrl, UriKind.Absolute);
        Log.Info($"拉取索引 {indexUrl}");
        var bytes = await _source.GetBytesAsync(indexUrl, ct).ConfigureAwait(false);
        var index = JsonDefaults.Deserialize<RootIndex>(bytes);

        if (index.SchemaVersion > RootIndex.CurrentSchema)
            throw new NotSupportedException(
                $"索引 schemaVersion={index.SchemaVersion} 高于本启动器支持的 {RootIndex.CurrentSchema}，请升级 {AppInfo.Name}");

        // 零游戏最常见的真实原因不是"内容源是空的"，而是地址填错了：
        // 把某个游戏的 manifest.json 当成了 index.json。那种 JSON 反序列化成 RootIndex
        // 不会抛异常，只是 games 为空 —— 用户只会看到"这个内容源里还没有游戏"，
        // 完全无从下手。所以在这里认出来并说清楚。
        if (index.Games.Count == 0)
        {
            var text = System.Text.Encoding.UTF8.GetString(bytes);

            if (text.Contains("\"gameId\"", StringComparison.Ordinal) || text.Contains("\"files\"", StringComparison.Ordinal))
                throw new InvalidDataException(
                    "这个地址返回的是某个游戏的清单（manifest.json），不是索引（index.json）。\n\n" +
                    "请把设置里的「内容源索引地址」改成以 /index.json 结尾，例如：\n" +
                    "  http://<你的服务器>:8787/index.json");

            Log.Warn("索引里没有任何游戏（内容源返回的 games 为空）");
        }

        Log.Info($"索引加载完成：{index.Games.Count} 个游戏");
        return index;
    }

    public async Task<GameManifest> LoadManifestAsync(GameEntry game, string channel, CancellationToken ct = default)
    {
        var pointer = game.Channel(channel)
            ?? throw new InvalidDataException($"游戏 {game.Id} 不存在通道 {channel}");

        _policy.AssertAllowed(pointer.ManifestUrl);
        AssertSourceConsistency(pointer.ManifestUrl);

        var bytes = await _source.GetBytesAsync(pointer.ManifestUrl, ct).ConfigureAwait(false);

        // 清单是"带内"信任根：先校验哈希，再解析。
        if (!string.IsNullOrWhiteSpace(pointer.ManifestSha256))
        {
            var actual = Hashing.Sha256Bytes(bytes);
            if (!Hashing.Equals(actual, pointer.ManifestSha256))
                throw new InvalidDataException(
                    $"清单哈希校验失败（可能被篡改或传输损坏）\n期望 {pointer.ManifestSha256}\n实际 {actual}");
        }

        var manifest = JsonDefaults.Deserialize<GameManifest>(bytes);

        if (manifest.SchemaVersion > GameManifest.CurrentSchema)
            throw new NotSupportedException(
                $"清单 schemaVersion={manifest.SchemaVersion} 高于本启动器支持的 {GameManifest.CurrentSchema}，请升级 {AppInfo.Name}");

        if (!VersionUtil.IsAtLeast(AppInfo.Version, manifest.LauncherMinVersion))
            throw new NotSupportedException(
                $"游戏 {manifest.Name} 需要启动器 {manifest.LauncherMinVersion} 或更高版本，当前 {AppInfo.Version}，请升级");

        // 提前把清单里的所有路径和文件 URL 过一遍安全校验：宁可现在失败，也不要下载到一半才发现。
        foreach (var file in manifest.Files)
        {
            SafePath.NormalizeRelative(file.Path);

            var fileUrl = file.ResolveUrl(manifest.BaseUrl);
            _policy.AssertAllowed(fileUrl);
            AssertSourceConsistency(fileUrl);
        }

        if (string.IsNullOrWhiteSpace(manifest.Launch.Executable))
            throw new InvalidDataException($"游戏 {manifest.GameId} 的清单未声明 launch.executable");

        if (manifest.Find(manifest.Launch.Executable) is null)
            throw new InvalidDataException(
                $"游戏 {manifest.GameId} 的 launch.executable=\"{manifest.Launch.Executable}\" 不在 files 清单中，拒绝安装");

        Log.Info($"清单加载完成：{manifest.Name} {manifest.Version}，{manifest.Files.Count} 个文件 {Hashing.HumanBytes(manifest.TotalBytes)}");
        return manifest;
    }

    /// <summary>
    /// 来源一致性：索引在一个远端主机上，清单却指向 127.0.0.1。
    ///
    /// 这是"把 -Local 打的包传到了服务器"的典型症状。不拦的话，客户端会去连玩家自己的机器：
    /// 轻则下载失败看不懂原因，重则（本机恰好跑着内容源时）静默从一份残留的本地副本安装。
    /// 与其让它表现成一个莫名其妙的连接错误，不如在这里说清楚是什么事、怎么修。
    /// </summary>
    private void AssertSourceConsistency(string url)
    {
        if (_indexUri is null || _indexUri.IsLoopback)
            return;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.IsLoopback)
            return;

        throw new InvalidDataException(
            $"内容源地址自相矛盾：索引来自 {_indexUri.Host}，但清单里的地址指向 {uri.Host}。\n\n" +
            "最常见的原因：拷贝到服务器上的是用 -Local 打的包（它的 URL 指向本机回环）。\n\n" +
            "修复：\n" +
            "  1) 在你电脑上重新执行   .\\publish-testpack.ps1      （不要加 -Local）\n" +
            "  2) 把 build\\site 重新传到服务器，覆盖旧文件\n" +
            "  3) 在启动器里点「刷新游戏库」");
    }
}
