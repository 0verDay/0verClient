using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OverClient.Core;
using OverClient.Core.Update;

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

        // --apply-update <目标exe> <旧进程PID>：旧版启动器在退出前把**新版自己**拉起来，
        // 由它完成"覆盖目标 exe → 重新启动"。
        //
        // 必须排在所有分支最前面：这条路只做文件搬运，绝不能创建主窗口 ——
        // 否则玩家会看到两个启动器窗口，而且窗口会占住我们正要替换的文件。
        var apply = LauncherUpdater.ParseApplyArgs(e.Args);

        if (apply is not null)
        {
            var code = LauncherUpdater.ApplyUpdate(apply.Value.TargetExe, apply.Value.ParentPid);
            Log.Info($"---- 更新模式结束，退出码 {code} ----");
            Environment.Exit(code);
            return;
        }

        // --screenshot [path]：把主窗口连同一张"悬停中"的卡片渲染成 PNG 然后退出。
        //
        // 存在的理由：描边被裁、间距不对这类问题**只有真实渲染才看得见**，
        // --selfcheck 只能证明"没抛异常"。改完 Theme.xaml / MainWindow.xaml 建议跑一次。
        if (e.Args.Any(a => a.StartsWith("--screenshot", StringComparison.OrdinalIgnoreCase)))
        {
            var code = RunScreenshot(e.Args);
            Log.Info($"---- 截图结束，退出码 {code} ----");
            Environment.Exit(code);
            return;
        }

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

    /// <summary>
    /// 把主窗口（含一张被强制成"悬停中"的卡片）渲染成 PNG。
    ///
    /// IsMouseOver 是只读依赖属性，无鼠标时触发器不会亮，所以这里直接取模板部件
    /// （Halo / HaloScale / Body）并把它们设成动画的**终值**，数值与 Theme.xaml 里的 To 一致。
    /// 改 Theme.xaml 里的动画数值时，这里也要跟着改，否则截出来的不是真实效果。
    /// </summary>
    private static int RunScreenshot(string[] args)
    {
        try
        {
            var path = ArgValue(args, "--screenshot") ?? Path.Combine(Path.GetTempPath(), "0verclient-ui.png");

            const int width = 1180;
            const int height = 720;

            var window = new MainWindow();

            // 4 张卡才能看出"一排卡片"的间距与描边是否被裁。
            for (var i = 0; i < 4; i++)
                window.ViewModel.AddSampleCardForSelfCheck();

            if (window.Content is not UIElement root)
            {
                Log.Error("SCREENSHOT FAILED: MainWindow 没有内容");
                return 1;
            }

            // 不调 Show()：Measure/Arrange 就足以把 ItemsControl 的容器和卡片模板实例化出来，
            // 也不会让窗口在屏幕上闪一下。
            root.Measure(new Size(width, height));
            root.Arrange(new Rect(0, 0, width, height));
            root.UpdateLayout();

            var hovered = ForceHoverOnFirstCard(root);

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);

            // 窗口背景是半透明的，直接存 PNG 会带 alpha 通道，看图时反而不好判断边界，压一层黑底。
            var composed = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, width, height));
                dc.DrawImage(bitmap, new Rect(0, 0, width, height));
            }
            composed.Render(visual);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(composed));

            var full = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);

            using (var stream = File.Create(full))
                encoder.Save(stream);

            Log.Info($"SCREENSHOT OK: {full}（强制悬停 {hovered} 张卡片）");
            Console.WriteLine(full);
            return hovered > 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Log.Error("SCREENSHOT FAILED", ex);
            return 1;
        }
    }

    private static int ForceHoverOnFirstCard(DependencyObject scope)
    {
        var card = FindFirstCard(scope);
        if (card is null)
        {
            Log.Warn("SCREENSHOT: 没找到游戏卡片，截图里不会有悬停效果");
            return 0;
        }

        card.ApplyTemplate();

        if (card.Template.FindName("Halo", card) is Border halo)
            halo.Opacity = 1;

        if (card.Template.FindName("HaloScale", card) is ScaleTransform scale)
        {
            scale.ScaleX = 1.022;
            scale.ScaleY = 1.022;
        }

        if (card.Template.FindName("Body", card) is Border body)
            body.Background = new SolidColorBrush(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF));

        card.UpdateLayout();
        return 1;
    }

    private static Button? FindFirstCard(DependencyObject scope)
    {
        if (scope is Button button && button.Width is > 200)
            return button;

        var count = VisualTreeHelper.GetChildrenCount(scope);

        for (var i = 0; i < count; i++)
        {
            var found = FindFirstCard(VisualTreeHelper.GetChild(scope, i));
            if (found is not null)
                return found;
        }

        return null;
    }

    private static string? ArgValue(string[] args, string name)
    {
        foreach (var arg in args)
        {
            if (arg.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                return arg[(name.Length + 1)..];
        }

        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) &&
                !args[i + 1].StartsWith('-'))
            {
                return args[i + 1];
            }
        }

        return null;
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
