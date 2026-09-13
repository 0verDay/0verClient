namespace OverClient.Core.Util;

/// <summary>
/// URL 准入策略：无后端方案里的第二道防线。
/// 即使 manifest 的签名密钥泄漏，攻击者也不能把客户端指向任意主机。
///
/// 默认（<see cref="Strict"/>）只允许 https，外加本机回环 http（方便本地 demo）。
/// 明文 http 需要显式打开，并且主机必须被列进白名单 —— 这两件事必须同时满足，
/// 目的是让"图省事放开 http"这个动作不可能意外发生。
/// </summary>
public sealed class UrlPolicy(IReadOnlyCollection<string> allowedHosts, bool allowInsecureHttp)
{
    /// <summary>默认策略：https + 本机回环 http，不限制主机。</summary>
    public static UrlPolicy Strict { get; } = new([], false);

    /// <summary>允许的主机（大小写不敏感）。为空表示不限制 https 主机。</summary>
    public IReadOnlyCollection<string> AllowedHosts { get; } = allowedHosts;

    /// <summary>是否放行非回环的明文 http。默认关闭。</summary>
    public bool AllowInsecureHttp { get; } = allowInsecureHttp;

    public void AssertAllowed(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new InvalidDataException($"非法 URL: {url}");

        var hostListed = AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
        var loopback = uri.IsLoopback;
        var isHttps = string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        var isHttp = string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);

        if (!isHttps && !isHttp)
            throw new InvalidDataException($"不支持的协议 {uri.Scheme}：{url}");

        // http 只在"本机回环"或"显式开关 + 主机显式列出"两种情况下放行。
        if (isHttp && !loopback)
        {
            if (!AllowInsecureHttp)
                throw new InvalidDataException(
                    $"拒绝明文 http：{url}\n" +
                    "内容源请使用 https；若只是本地/测试环境，请在设置里打开\"允许明文 http\"并把主机加入白名单。");

            if (!hostListed)
                throw new InvalidDataException(
                    $"已允许明文 http，但主机 {uri.Host} 不在白名单里，拒绝：{url}\n" +
                    "放行 http 时必须显式列出主机，这样不会因为一个开关就把所有主机都放开。");

            Log.Warn($"使用明文 http 下载（已在设置中显式放行）：{uri.Host}");
        }

        // 白名单只约束"外部主机"。回环地址天然属于本机，永远允许 ——
        // 否则在"本地调试"和"连服务器"两种模式之间切换时得反复改白名单，很容易误判成 bug。
        // 真正要防的"索引在服务器、文件却指向 127.0.0.1"那种情况，由 ManifestService
        // 的来源一致性检查负责，那里给出的报错也更有指向性。
        if (AllowedHosts.Count > 0 && !hostListed && !loopback)
            throw new InvalidDataException(
                $"URL 主机不在白名单内：{uri.Host}\n" +
                $"当前白名单：{(AllowedHosts.Count == 0 ? "(空)" : string.Join(", ", AllowedHosts))}\n" +
                "请到设置页把该主机加进白名单（只填主机名或 IP，不要带 http:// 和端口）。");
    }
}
