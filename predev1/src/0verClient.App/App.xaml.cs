using System.Windows;
using System.Windows.Threading;
using OverClient.Core;

namespace OverClient.App;

public partial class App : Application
{
    /// <summary>两次错误弹窗之间的最小间隔。</summary>
    private static readonly TimeSpan ErrorDialogCooldown = TimeSpan.FromSeconds(10);

    private int _unhandledCount;
    private DateTime _lastErrorDialogUtc = DateTime.MinValue;

    protected override void OnStartup(StartupEventArgs e)
    {
        Log.Init();
        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("域级未处理异常", args.ExceptionObject as Exception);

        // --selfcheck：把主窗口的 XAML 真正实例化一遍（包含游戏卡片模板），然后退出。
        //
        // 为什么需要它：XAML 的绑定模式错误是**运行时**才抛的，编译 0 error。
        // 例如 ProgressBar.Value 的元数据是 BindsTwoWayByDefault，绑到只读的 Progress
        // 就会每张卡片抛一次 XamlParseException —— 而示例里一旦没有游戏卡片，
        // 这个错误就永远不出现，直到内容源终于有游戏为止。
        if (e.Args.Any(a => string.Equals(a, "--selfcheck", StringComparison.OrdinalIgnoreCase)))
        {
            var code = RunSelfCheck();
            Log.Info($"---- 自检结束，退出码 {code} ----");
            Environment.Exit(code);
            return;
        }

        Log.Info("---- 启动器启动 ----");

        // 不用 StartupUri：显式建窗可以保证自检路径不会顺手把主窗口也拉起来。
        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        base.OnStartup(e);
    }

    private static int RunSelfCheck()
    {
        try
        {
            Log.Info("SELFCHECK: 实例化 MainWindow（解析全部 XAML）");
            var window = new MainWindow();

            Log.Info("SELFCHECK: 注入示例卡片并强制布局（实例化卡片模板）");
            window.ViewModel.AddSampleCardForSelfCheck();

            if (window.Content is not UIElement root)
            {
                Log.Error("SELFCHECK FAILED: MainWindow 没有内容");
                return 1;
            }

            root.Measure(new Size(1180, 720));
            root.Arrange(new Rect(0, 0, 1180, 720));
            root.UpdateLayout();

            Log.Info("SELFCHECK OK: 全部 XAML 与卡片模板实例化成功，没有绑定模式错误");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("SELFCHECK FAILED", ex);
            return 1;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("---- 启动器退出 ----");
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var count = Interlocked.Increment(ref _unhandledCount);

        // 完整信息（含堆栈）永远进日志。
        Log.Error($"UI 线程未处理异常（本次会话第 {count} 次）", e.Exception);

        // 但对话框必须限流：绑定错误会按"每个元素一次"的频率抛出，
        // 无限制弹模态框会把客户端彻底冻住（这正是作者自己踩过的坑）。
        var now = DateTime.UtcNow;
        if (now - _lastErrorDialogUtc < ErrorDialogCooldown)
        {
            e.Handled = true;
            return;
        }

        _lastErrorDialogUtc = now;

        MessageBox.Show(
            $"发生未处理的错误（本次会话第 {count} 次）：\n\n{e.Exception.Message}\n\n" +
            $"完整堆栈已写入日志：\n{Core.Util.AppPaths.Logs}\n\n" +
            $"{ErrorDialogCooldown.TotalSeconds:0} 秒内的后续错误只写日志，不再弹窗。",
            Core.Util.AppInfo.Name,
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        // 演示阶段不让一个异常直接带走整个启动器。
        e.Handled = true;
    }
}
