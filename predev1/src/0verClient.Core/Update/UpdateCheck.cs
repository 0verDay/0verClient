using OverClient.Core.Install;
using OverClient.Core.Manifest;
using OverClient.Core.Util;

namespace OverClient.Core.Update;

/// <summary>
/// 启动器更新的判定结果。
/// 做成不可变记录是为了能被 SmokeTest 直接断言 —— 判定逻辑不允许有 UI 依赖。
/// </summary>
public sealed record LauncherUpdateDecision(
    bool Available,
    bool Mandatory,
    string Version,
    string? Url,
    string? Sha256,
    long Size,
    string? Notes)
{
    public static readonly LauncherUpdateDecision None = new(false, false, "", null, null, 0, null);

    /// <summary>
    /// 能不能一键更新。必须有下载地址**和** sha256：
    /// 缺少哈希就只提示、不自动替换 —— 否则等于把"服务器说什么就装什么"变成默认行为。
    /// </summary>
    public bool CanAutoApply =>
        Available && !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(Sha256);
}

/// <summary>
/// "有没有更新"的纯判定，不碰网络也不碰磁盘。
/// 放在 Core 而不是 ViewModel 里，就是为了让 SmokeTest 能无头把边界情况全跑一遍。
/// </summary>
public static class UpdateCheck
{
    /// <summary>
    /// 游戏是否有更新。两个条件任一成立即算有更新：
    ///
    ///   1) 通道里的版本号比已安装的新；
    ///   2) 版本号相同，但清单 sha256 与安装时记录的不一样。
    ///
    /// 第 2 条是刻意加的：发布者忘记改版本号是常态，只比版本号会让这类更新**永远发不出去**
    /// （而且症状是"我明明重传了，玩家却一直没反应"，极难排查）。
    /// 安装记录里没有 sha（老版本写下的 state 文件）时，退化为只比版本号。
    /// </summary>
    public static bool IsGameUpdateAvailable(InstalledGame? installed, ChannelEntry? available)
    {
        if (installed is null || available is null)
            return false;

        var comparison = VersionUtil.Compare(available.Version, installed.Version);

        if (comparison > 0)
            return true;

        // 本地版本比服务器还新：这是"回滚"或用户装过 beta，不该提示更新。
        if (comparison < 0)
            return false;

        if (string.IsNullOrWhiteSpace(installed.ManifestSha256) ||
            string.IsNullOrWhiteSpace(available.ManifestSha256))
            return false;

        return !Hashing.Equals(installed.ManifestSha256, available.ManifestSha256);
    }

    /// <summary>
    /// 启动器自身是否有更新。站点没有 latest.json（release 为 null）时一律返回 None ——
    /// 那是正常情况，不是错误。
    /// </summary>
    public static LauncherUpdateDecision CheckLauncher(string currentVersion, LauncherRelease? release)
    {
        if (release is null || string.IsNullOrWhiteSpace(release.Version))
            return LauncherUpdateDecision.None;

        if (VersionUtil.Compare(release.Version, currentVersion) <= 0)
            return LauncherUpdateDecision.None;

        var mandatory = !string.IsNullOrWhiteSpace(release.MinVersion)
                        && VersionUtil.Compare(currentVersion, release.MinVersion) < 0;

        return new LauncherUpdateDecision(
            Available: true,
            Mandatory: mandatory,
            Version: release.Version,
            Url: release.Url,
            Sha256: release.Sha256,
            Size: release.Size,
            Notes: release.Notes);
    }
}
