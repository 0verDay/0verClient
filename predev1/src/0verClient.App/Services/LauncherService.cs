using OverClient.Core;
using OverClient.Core.Install;
using OverClient.Core.Launch;
using OverClient.Core.Manifest;
using OverClient.Core.Net;
using OverClient.Core.Update;
using OverClient.Core.Util;

namespace OverClient.App.Services;

/// <summary>
/// 把 Core 的各个部件接起来，并持有它们的生命周期。
/// UI 只跟这一层打交道，永远不直接 new 出 HttpClient 或 Installer。
/// </summary>
public sealed class LauncherService : IDisposable
{
    private readonly HttpContentSource _source = new();
    private readonly Installer _installer;
    private ManifestService _manifests;
    private bool _disposed;

    public AppSettings Settings { get; }
    public StateStore State { get; }
    public GameLauncher Launcher { get; } = new();

    public string GamesRoot => AppPaths.Games;

    public LauncherService()
    {
        Settings = AppSettings.Load();
        Launcher.KillGamesOnExit = Settings.KillGamesOnExit;
        _manifests = new ManifestService(_source, Settings.BuildUrlPolicy());
        _installer = new Installer(_source);
        State = new StateStore(AppPaths.State);
    }

    public void SaveSettings()
    {
        Launcher.KillGamesOnExit = Settings.KillGamesOnExit;
        Settings.Save();

        // URL 放行策略可能变了，重建清单服务让新策略立刻生效（下次刷新游戏库就是新策略）。
        _manifests = new ManifestService(_source, Settings.BuildUrlPolicy());
        Log.Info($"内容源访问策略已刷新：{Settings.DescribePolicy()}");
    }

    public Task<RootIndex> LoadIndexAsync(CancellationToken ct = default) =>
        _manifests.LoadIndexAsync(Settings.IndexUrl, ct);

    public Task<GameManifest> LoadManifestAsync(GameEntry entry, CancellationToken ct = default) =>
        _manifests.LoadManifestAsync(entry, Settings.Channel, ct);

    /// <summary>
    /// 取站点里的 latest.json（启动器自身的更新信息）。
    /// 拿不到就返回 null —— 站点不提供启动器更新是正常情况，不是错误。
    /// </summary>
    public Task<LauncherRelease?> LoadLauncherReleaseAsync(CancellationToken ct = default) =>
        _manifests.LoadLauncherReleaseAsync(Settings.IndexUrl, ct);

    /// <summary>下载并校验新版启动器，返回它的磁盘路径。校验不过会抛异常。</summary>
    public Task<string> DownloadLauncherUpdateAsync(
        LauncherUpdateDecision decision,
        IProgress<LauncherDownloadProgress>? progress = null,
        CancellationToken ct = default) =>
        LauncherUpdater.DownloadAsync(_source, decision, progress, ct);

    public Task<InstallResult> InstallAsync(
        GameManifest manifest,
        IProgress<InstallProgress> progress,
        CancellationToken ct = default) =>
        _installer.InstallAsync(manifest, AppPaths.Games, progress, ct);

    /// <summary>把一次安装结果落盘成"已安装"记录。</summary>
    public InstalledGame Persist(InstallResult result)
    {
        var record = State.FromResult(result);

        // 已存在则保留历史游玩时长与最后游玩时间。
        var previous = State.Get(result.GameId);
        if (previous is not null)
        {
            record.PlaySeconds = previous.PlaySeconds;
            record.LastPlayedAt = previous.LastPlayedAt;
        }

        State.Save(record);
        return record;
    }

    public void SavePlayTime(string gameId, TimeSpan duration)
    {
        var record = State.Get(gameId);
        if (record is null)
            return;

        record.PlaySeconds += duration.TotalSeconds;
        record.LastPlayedAt = DateTimeOffset.UtcNow;
        State.Save(record);
        Log.Info($"记录游玩时长 {gameId} +{duration.TotalSeconds:0}s（累计 {record.PlaySeconds:0}s）");
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Launcher.Dispose();
        _source.Dispose();
    }
}
