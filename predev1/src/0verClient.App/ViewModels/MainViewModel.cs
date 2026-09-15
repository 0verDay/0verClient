using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using OverClient.App.Mvvm;
using OverClient.App.Services;
using OverClient.App.Theme;
using OverClient.Core;
using OverClient.Core.Update;
using OverClient.Core.Util;

namespace OverClient.App.ViewModels;

/// <summary>设置页里主题下拉框的一项。<see cref="Value"/> 是存进 settings.json 的字符串。</summary>
public sealed record ThemeOption(string Value, string Display);

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly LauncherService _service = new();
    private int _navIndex;
    private bool _isBusy;
    private string _statusText = "就绪";
    private string _indexUrl;
    private bool _allowInsecureHttp;
    private string _allowedHosts = "";
    private string _theme;
    private string _lastError = "";
    private LauncherUpdateDecision _launcherUpdate = LauncherUpdateDecision.None;
    private bool _isUpdatingLauncher;
    private string _launcherProgress = "";

    public MainViewModel()
    {
        _indexUrl = _service.Settings.IndexUrl;
        _allowInsecureHttp = _service.Settings.AllowInsecureHttp;
        _allowedHosts = _service.Settings.AllowedHosts;

        // 以 ThemeManager 当前实际挂着的主题为准，而不是设置文件里的字符串：
        // 启动参数 --theme 可以覆盖设置，两者不一致时界面必须显示"真正在用的那个"。
        _theme = ThemeManager.ToSettingValue(ThemeManager.Current);

        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync(), _ => !_isBusy);
        SaveSettingsCommand = new RelayCommand(_ => SaveSettings());
        OpenDataFolderCommand = new RelayCommand(_ => OpenPath(AppPaths.Root));
        OpenGamesFolderCommand = new RelayCommand(_ => OpenPath(_service.GamesRoot));
        UpdateLauncherCommand = new AsyncRelayCommand(_ => UpdateLauncherAsync(), _ => CanApplyLauncherUpdate);

        _service.Launcher.GameExited += OnGameExited;
        ThemeManager.Changed += OnThemeChanged;
    }

    public ObservableCollection<GameCardViewModel> Games { get; } = [];

    public ObservableCollection<GameCardViewModel> Downloads { get; } = [];

    /// <summary>主题下拉框的固定选项。顺序就是界面上的顺序。</summary>
    public IReadOnlyList<ThemeOption> ThemeOptions { get; } =
    [
        new("dark", "深色模式"),
        new("light", "浅色模式")
    ];

    public ICommand RefreshCommand { get; }
    public ICommand SaveSettingsCommand { get; }
    public ICommand OpenDataFolderCommand { get; }
    public ICommand OpenGamesFolderCommand { get; }
    public ICommand UpdateLauncherCommand { get; }

    public int NavIndex
    {
        get => _navIndex;
        set
        {
            if (!Set(ref _navIndex, value))
                return;

            Raise(nameof(IsLibraryVisible));
            Raise(nameof(IsDownloadsVisible));
            Raise(nameof(IsSettingsVisible));
            Raise(nameof(PageTitle));
            Raise(nameof(PageSubtitle));
            Raise(nameof(ShowLibraryEmptyState));
        }
    }

    /// <summary>空库时给一句人话，而不是只在状态栏留一行小字。</summary>
    public bool ShowLibraryEmptyState => NavIndex == 0 && Games.Count == 0;

    public string EmptyStateTitle => string.IsNullOrEmpty(_lastError)
        ? "这个内容源里还没有游戏"
        : "读取内容源失败";

    public string EmptyStateDetail
    {
        get
        {
            var lines = new List<string>();

            lines.Add(string.IsNullOrEmpty(_lastError)
                ? "索引读取成功了，但里面一个游戏都没有。"
                : _lastError);

            lines.Add($"索引地址：{IndexUrl}");

            if (IndexUrl.Contains("manifest.json", StringComparison.OrdinalIgnoreCase))
                lines.Add("⚠ 地址里出现了 manifest.json —— 这里要填的是索引地址 index.json，不是某个游戏的清单。");

            lines.Add("");
            lines.Add("本地 demo 需要内容源一直在运行：在 predev1 目录执行 .\\run-demo.ps1；"
                    + "那个最小化的内容源窗口一关，游戏库就会变空。");
            lines.Add("连服务器时，请先用浏览器打开上面的索引地址确认它能返回 JSON。");

            return string.Join("\n", lines);
        }
    }

    public bool IsLibraryVisible => NavIndex == 0;
    public bool IsDownloadsVisible => NavIndex == 1;
    public bool IsSettingsVisible => NavIndex == 2;

    public string PageTitle => NavIndex switch
    {
        1 => "下载",
        2 => "设置",
        _ => "游戏库"
    };

    public string PageSubtitle => NavIndex switch
    {
        1 => "正在进行的任务会显示在这里，关掉卡片不会中断下载队列",
        2 => "内容源地址、通道与数据目录",
        _ => "全部游戏都来自静态 manifest，启动器本身不内置任何游戏"
    };

    public string IndexUrl
    {
        get => _indexUrl;
        set => Set(ref _indexUrl, value);
    }

    /// <summary>调试用：放行非回环明文 http（主机仍需在白名单里）。</summary>
    public bool AllowInsecureHttp
    {
        get => _allowInsecureHttp;
        set => Set(ref _allowInsecureHttp, value);
    }

    public string AllowedHosts
    {
        get => _allowedHosts;
        set => Set(ref _allowedHosts, value);
    }

    /// <summary>
    /// 设置页里选中的主题（"dark" / "light"）。
    ///
    /// 这个 setter 刻意**立刻切换并落盘**，而不是等用户点「保存」：
    /// 主题是所见即所得的东西，选完还要再点一次保存才能看到效果很别扭。
    /// 其余设置（索引地址、白名单）仍然只在点「保存」时落盘，因为它们是"改了会影响
    /// 下一次刷新"的配置，需要显式确认。
    /// </summary>
    public string SelectedTheme
    {
        get => _theme;
        set
        {
            var normalized = ThemeManager.NormalizeSetting(value);

            if (!Set(ref _theme, normalized))
                return;

            ThemeManager.Apply(normalized);
            _service.Settings.Theme = normalized;
            _service.SaveSettings();

            Raise(nameof(ThemeHint));
            StatusText = normalized == "light" ? "已切换到浅色模式" : "已切换到深色模式";
        }
    }

    public string ThemeHint => ThemeManager.IsLight
        ? "当前：浅色模式。窗口底色接近不透明，保证深色文字在亮色桌面上依然清晰。"
        : "当前：深色模式。半透明深色外壳，配合桌面背景。";

    public string PolicyHint => _service.Settings.DescribePolicy();

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public string DataRoot => AppPaths.Root;
    public string GamesRoot => _service.GamesRoot;
    public string LogsRoot => AppPaths.Logs;
    public string Channel => _service.Settings.Channel;
    public string LauncherVersion => AppInfo.Version;

    // ---------------------------------------------------------------
    // 启动器自更新
    // ---------------------------------------------------------------

    public bool IsLauncherUpdateAvailable => _launcherUpdate.Available;

    public bool IsUpdatingLauncher => _isUpdatingLauncher;

    /// <summary>没有可校验的下载地址时不允许一键更新，只提示。</summary>
    public bool CanApplyLauncherUpdate => _launcherUpdate.CanAutoApply && !_isUpdatingLauncher;

    public string LauncherUpdateActionText => _isUpdatingLauncher ? "更新中…" : "立即更新";

    public string LauncherUpdateText
    {
        get
        {
            if (!_launcherUpdate.Available)
                return "";

            var text = _isUpdatingLauncher
                ? $"正在下载启动器 v{_launcherUpdate.Version}…"
                : _launcherUpdate.Mandatory
                    ? $"当前版本 {AppInfo.Version} 已不受支持，必须更新到 v{_launcherUpdate.Version}"
                    : $"发现启动器新版本 v{_launcherUpdate.Version}（当前 {AppInfo.Version}）";

            if (_isUpdatingLauncher && !string.IsNullOrEmpty(_launcherProgress))
                text += $" · {_launcherProgress}";

            if (!_launcherUpdate.CanAutoApply && !_isUpdatingLauncher)
                text += " · 该内容源没有提供可校验的下载地址，请手动更新";

            if (!string.IsNullOrWhiteSpace(_launcherUpdate.Notes))
                text += $"\n{_launcherUpdate.Notes}";

            return text;
        }
    }

    public async Task InitializeAsync() => await RefreshAsync().ConfigureAwait(true);

    /// <summary>
    /// 无界面自检用：塞一张假卡片，逼 WPF 把卡片模板实例化一次。
    ///
    /// 存在的理由很具体：绑定模式错误（只读属性 + 目标属性默认 TwoWay）只在模板
    /// 实例化那一刻抛，编译期看不出来。而卡片只在游戏库非空时才渲染 ——
    /// 所以内容源没起来的时候，这个 bug 会一直躲着。
    /// </summary>
    internal void AddSampleCardForSelfCheck()
    {
        var entry = new Core.Manifest.GameEntry
        {
            Id = "selfcheck",
            Name = "Self Check",
            Summary = "只用于实例化卡片模板，不会真的安装。",
            AccentColor = "#4C8DFF",
            Tags = ["selfcheck"]
        };

        var card = new GameCardViewModel(entry, _service, null);
        Games.Add(card);
        Downloads.Add(card);
        Raise(nameof(ShowLibraryEmptyState));
    }

    private async Task RefreshAsync()
    {
        _isBusy = true;
        StatusText = "正在读取索引…";
        _lastError = "";

        try
        {
            var index = await _service.LoadIndexAsync().ConfigureAwait(true);

            // 顺手检查启动器自身有没有新版。失败/站点没有 latest.json 都只是"没有更新"，
            // 绝不能让游戏库刷新跟着失败 —— 所以它内部自己吞掉所有异常。
            await CheckLauncherUpdateAsync().ConfigureAwait(true);

            Games.Clear();
            foreach (var entry in index.Games)
            {
                var card = new GameCardViewModel(entry, _service, _service.State.Get(entry.Id));
                card.PropertyChanged += (_, args) =>
                {
                    // 只在"是否正在安装"翻转时重建下载列表，别让进度更新把它刷爆。
                    if (args.PropertyName == nameof(GameCardViewModel.IsInstalling))
                        SyncDownloads();
                };
                Games.Add(card);
            }

            SyncDownloads();
            StatusText = $"已加载 {Games.Count} 个游戏 · 数据目录 {AppPaths.Root}";
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            StatusText = $"读取索引失败：{ex.Message}";
            Log.Error("读取索引失败", ex);
        }
        finally
        {
            _isBusy = false;
            Raise(nameof(ShowLibraryEmptyState));
            Raise(nameof(EmptyStateTitle));
            Raise(nameof(EmptyStateDetail));
        }
    }

    private async Task CheckLauncherUpdateAsync()
    {
        try
        {
            var release = await _service.LoadLauncherReleaseAsync().ConfigureAwait(true);
            _launcherUpdate = UpdateCheck.CheckLauncher(AppInfo.Version, release);

            if (_launcherUpdate.Available)
            {
                Log.Info($"发现启动器新版本 {_launcherUpdate.Version}"
                       + $"（强制={_launcherUpdate.Mandatory}，可一键更新={_launcherUpdate.CanAutoApply}）");
            }
        }
        catch (Exception ex)
        {
            // 检查更新本身永远不该是致命错误。
            _launcherUpdate = LauncherUpdateDecision.None;
            Log.Warn($"检查启动器更新失败（按无更新处理）：{ex.Message}");
        }
        finally
        {
            RaiseLauncherUpdateState();
        }
    }

    /// <summary>
    /// 一键更新启动器：下载 → 校验 sha256 → 交给新版接管 → 自己退出。
    ///
    /// 顺序很重要：必须先启动新版、再退出。反过来的话新版启动时目标 exe 仍被占用，
    /// 它要白白等到超时才发现可以替换。
    /// </summary>
    private async Task UpdateLauncherAsync()
    {
        if (!CanApplyLauncherUpdate)
            return;

        _isUpdatingLauncher = true;
        _launcherProgress = "";
        RaiseLauncherUpdateState();

        try
        {
            var progress = new Progress<LauncherDownloadProgress>(p =>
            {
                _launcherProgress = $"{p.DetailText} {p.Percent}%";
                Raise(nameof(LauncherUpdateText));
            });

            var staged = await _service.DownloadLauncherUpdateAsync(_launcherUpdate, progress).ConfigureAwait(true);

            StatusText = $"启动器 v{_launcherUpdate.Version} 已下载并校验，正在重启完成替换…";

            LauncherUpdater.HandOver(staged, Environment.ProcessId);

            Application.Current?.Shutdown();
        }
        catch (Exception ex)
        {
            _isUpdatingLauncher = false;
            _launcherProgress = "";
            StatusText = $"启动器更新失败：{ex.Message}";
            Log.Error("启动器自更新失败", ex);
            RaiseLauncherUpdateState();

            MessageBox.Show(
                $"启动器更新失败：\n\n{ex.Message}\n\n当前版本继续可用，日志：{AppPaths.Logs}",
                "0verClient · 更新失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void RaiseLauncherUpdateState()
    {
        Raise(nameof(IsLauncherUpdateAvailable));
        Raise(nameof(IsUpdatingLauncher));
        Raise(nameof(CanApplyLauncherUpdate));
        Raise(nameof(LauncherUpdateActionText));
        Raise(nameof(LauncherUpdateText));

        // WPF 不会自己发现 CanApplyLauncherUpdate 变了 —— 不显式通知的话，
        // 更新检查完成之后按钮仍然是灰的。
        if (UpdateLauncherCommand is AsyncRelayCommand command)
            command.RaiseCanExecuteChanged();
    }

    private void SyncDownloads()
    {
        Downloads.Clear();
        foreach (var card in Games.Where(g => g.IsInstalling))
            Downloads.Add(card);
    }

    private void SaveSettings()
    {
        _service.Settings.IndexUrl = IndexUrl;
        _service.Settings.AllowInsecureHttp = AllowInsecureHttp;
        _service.Settings.AllowedHosts = AllowedHosts;
        _service.Settings.Theme = SelectedTheme;
        _service.SaveSettings();

        Raise(nameof(Channel));
        Raise(nameof(PolicyHint));
        Raise(nameof(ThemeHint));
        StatusText = $"设置已保存 · {_service.Settings.DescribePolicy()} · 点「刷新游戏库」生效";
    }

    /// <summary>
    /// 主题被别处改掉时（目前只有设置页自己，但将来可能有快捷键/跟随系统）同步下拉框。
    /// 没有这层的话，SelectedTheme 会停在旧值，下拉框显示的主题和实际外观不一致。
    /// </summary>
    private void OnThemeChanged(object? sender, EventArgs e)
    {
        var current = ThemeManager.ToSettingValue(ThemeManager.Current);

        if (string.Equals(_theme, current, StringComparison.Ordinal))
            return;

        _theme = current;
        Raise(nameof(SelectedTheme));
        Raise(nameof(ThemeHint));
    }

    private void OnGameExited(object? sender, Core.Launch.GameExitEventArgs e)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
            return;

        dispatcher.Invoke(() =>
        {
            _service.SavePlayTime(e.GameId, e.Duration);

            var card = Games.FirstOrDefault(g =>
                string.Equals(g.Entry.Id, e.GameId, StringComparison.OrdinalIgnoreCase));

            card?.OnGameExited(e.Duration, e.Crashed);

            StatusText = e.Crashed
                ? $"游戏 {card?.Name ?? e.GameId} 异常退出（code {e.ExitCode}）"
                : $"游戏 {card?.Name ?? e.GameId} 已退出，本次游玩 {e.Duration.TotalSeconds:0} 秒";
        });
    }

    private static void OpenPath(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"打开目录失败 {path}", ex);
        }
    }

    public void Dispose()
    {
        // 静态事件（ThemeManager.Changed）必须退订：视图模型被重建而订阅留着的话，
        // 下一次切主题会回调到已经死掉的实例上。
        ThemeManager.Changed -= OnThemeChanged;
        _service.Dispose();
    }
}
