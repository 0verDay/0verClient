using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using OverClient.App.Mvvm;
using OverClient.App.Services;
using OverClient.Core;
using OverClient.Core.Install;
using OverClient.Core.Manifest;
using OverClient.Core.Util;

namespace OverClient.App.ViewModels;

public enum GameStatus
{
    NotInstalled,
    Installing,
    Installed,
    Running,
    Failed
}

/// <summary>
/// 游戏库里的一张卡，状态机收在这里。
/// 注意 ActionCommand 刻意用同步 RelayCommand 而不是 AsyncRelayCommand：
/// 异步命令在执行期间会把自己禁用掉，那样"安装中 → 点取消"就永远点不动了。
/// </summary>
public sealed class GameCardViewModel : ObservableObject
{
    private readonly LauncherService _service;
    private CancellationTokenSource? _cts;

    private GameStatus _status = GameStatus.NotInstalled;
    private double _progress;
    private string _statusText = "未安装";
    private string _detailText = "";

    public GameCardViewModel(GameEntry entry, LauncherService service, InstalledGame? installed)
    {
        Entry = entry;
        _service = service;
        Installed = installed;
        ActionCommand = new RelayCommand(_ => OnAction());

        if (installed is not null)
        {
            Status = GameStatus.Installed;
            Progress = 100;
            StatusText = $"已安装 v{installed.Version}";
            DetailText = installed.PlayTimeText;
        }
        else
        {
            var channel = entry.DefaultChannel();
            StatusText = channel is null ? "未安装" : $"未安装 · 可获取 v{channel.Version}";
        }
    }

    public GameEntry Entry { get; }

    public InstalledGame? Installed { get; private set; }

    public string Name => string.IsNullOrWhiteSpace(Entry.Name) ? Entry.Id : Entry.Name;

    public string Summary => string.IsNullOrWhiteSpace(Entry.Summary) ? "暂无简介" : Entry.Summary!;

    public string TagText => Entry.Tags.Count == 0 ? "" : string.Join("  ·  ", Entry.Tags);

    public string Initial => string.IsNullOrEmpty(Name) ? "?" : Name[..1].ToUpperInvariant();

    /// <summary>封面暂时用渐变占位：demo 不含美术资源，也避免引入图像解码依赖。</summary>
    public Brush CoverBrush
    {
        get
        {
            var accent = ParseColor(Entry.AccentColor, Color.FromRgb(0x4C, 0x8D, 0xFF));
            var brush = new LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(0, 0),
                EndPoint = new System.Windows.Point(1, 1)
            };
            brush.GradientStops.Add(new GradientStop(accent, 0));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x0E, 0x10, 0x16), 1));
            brush.Freeze();
            return brush;
        }
    }

    public GameStatus Status
    {
        get => _status;
        private set
        {
            if (!Set(ref _status, value))
                return;

            Raise(nameof(IsInstalling));
            Raise(nameof(IsInstalled));
            Raise(nameof(StatusBrush));
            Raise(nameof(ActionText));
        }
    }

    public bool IsInstalling => Status == GameStatus.Installing;

    public bool IsInstalled => Status is GameStatus.Installed or GameStatus.Running;

    public double Progress
    {
        get => _progress;
        private set => Set(ref _progress, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public string DetailText
    {
        get => _detailText;
        private set => Set(ref _detailText, value);
    }

    public string ActionText => Status switch
    {
        GameStatus.Installing => "取消",
        GameStatus.Installed => "启动",
        GameStatus.Running => "结束",
        GameStatus.Failed => "重试",
        _ => "安装"
    };

    public Brush StatusBrush => Status switch
    {
        GameStatus.Installed => new SolidColorBrush(Color.FromRgb(0x5C, 0xD6, 0x8A)),
        GameStatus.Running => new SolidColorBrush(Color.FromRgb(0x7F, 0xB0, 0xFF)),
        GameStatus.Failed => new SolidColorBrush(Color.FromRgb(0xF2, 0x6D, 0x6D)),
        GameStatus.Installing => new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xFF)),
        _ => new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80))
    };

    public ICommand ActionCommand { get; }

    /// <summary>游戏进程退出后回填状态（由 MainViewModel 在 UI 线程调用）。</summary>
    public void OnGameExited(TimeSpan duration, bool crashed)
    {
        if (crashed)
        {
            Status = GameStatus.Failed;
            StatusText = "游戏异常退出";
            DetailText = $"运行 {duration.TotalSeconds:0} 秒后退出，详情见日志";
            return;
        }

        Status = GameStatus.Installed;
        Installed = _service.State.Get(Entry.Id);
        StatusText = Installed is null ? "已安装" : $"已安装 v{Installed.Version}";
        DetailText = Installed?.PlayTimeText ?? $"本次游玩 {duration.TotalSeconds:0} 秒";
    }

    private void MarkRunning()
    {
        Status = GameStatus.Running;
        StatusText = "运行中";
        DetailText = "游戏已启动，关闭启动器不会结束游戏";
    }

    private void OnAction()
    {
        switch (Status)
        {
            case GameStatus.Installing:
                _cts?.Cancel();
                StatusText = "正在取消…";
                return;

            case GameStatus.Running:
                _service.Launcher.Kill(Entry.Id);
                return;

            case GameStatus.Installed:
                LaunchGame();
                return;

            default:
                // 即发即忘是安全的：InstallAsync 内部已经完整捕获所有异常。
                _ = InstallAsync();
                return;
        }
    }

    private void LaunchGame()
    {
        try
        {
            var record = _service.State.Get(Entry.Id) ?? Installed;
            if (record is null)
                throw new InvalidOperationException("没有找到安装记录，请重新安装");

            _service.Launcher.Launch(record);
            MarkRunning();
        }
        catch (Exception ex)
        {
            Status = GameStatus.Failed;
            StatusText = "启动失败";
            DetailText = FirstLine(ex.Message);
            Log.Error($"启动 {Entry.Id} 失败", ex);
            ShowFailure("启动失败", ex.Message);
        }
    }

    private async Task InstallAsync()
    {
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        Status = GameStatus.Installing;
        Progress = 0;
        StatusText = "准备中";
        DetailText = "";

        try
        {
            var manifest = await _service.LoadManifestAsync(Entry, token).ConfigureAwait(true);

            var progress = new Progress<InstallProgress>(p =>
            {
                Progress = p.Fraction * 100;
                StatusText = $"{p.PhaseText} {p.Percent}%";
                DetailText = string.IsNullOrEmpty(p.EtaText) ? p.DetailText : $"{p.DetailText} · {p.EtaText}";
            });

            var result = await _service.InstallAsync(manifest, progress, token).ConfigureAwait(true);
            Installed = _service.Persist(result);

            Progress = 100;
            Status = GameStatus.Installed;
            StatusText = $"已安装 v{result.Version}";
            DetailText = result.ReusedFiles > 0
                ? $"下载 {result.DownloadedFiles} 个 · 复用 {result.ReusedFiles} 个 · {Hashing.HumanBytes(result.TotalBytes)}"
                : $"{result.DownloadedFiles} 个文件 · {Hashing.HumanBytes(result.TotalBytes)}";
        }
        catch (OperationCanceledException)
        {
            Status = GameStatus.NotInstalled;
            Progress = 0;
            StatusText = "已取消";
            DetailText = "已下载的分片会保留，下次可断点续传";
        }
        catch (Exception ex)
        {
            Status = GameStatus.Failed;
            StatusText = "安装失败";
            DetailText = FirstLine(ex.Message);
            Log.Error($"安装 {Entry.Id} 失败", ex);

            // 卡片只有 230px 宽，长错误会被裁掉，用户只能看到半句话。
            // 这类失败是用户主动操作触发的，弹一次对话框不会造成刷屏。
            ShowFailure("安装失败", ex.Message);
        }
    }

    /// <summary>卡片上只放第一行，完整内容走日志和对话框。</summary>
    private static string FirstLine(string text)
    {
        var line = text.Split('\n')[0].Trim();
        return line.Length > 58 ? string.Concat(line.AsSpan(0, 58), "…") : line;
    }

    private static void ShowFailure(string title, string message)
    {
        try
        {
            MessageBox.Show(message, $"0verClient · {title}", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch
        {
            // 弹窗失败绝不能反过来影响状态机。
        }
    }

    private static Color ParseColor(string? value, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        try
        {
            return (Color)ColorConverter.ConvertFromString(value)!;
        }
        catch
        {
            return fallback;
        }
    }
}
