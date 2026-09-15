using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OverClient.App.Services;
using OverClient.App.Theme;
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

        // 主题必须在任何窗口之前定下来：窗口第一次解析 XAML 时就要取到正确颜色，
        // 否则会先按默认深色画一遍再变色（启动时闪一下）。
        //
        // 优先用启动参数 --theme dark|light（自检/截图用），否则读用户设置。
        var themeArg = ArgValue(e.Args, "--theme");
        ThemeManager.Apply(themeArg ?? AppSettings.Load().Theme);

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

        // --selfcheck-themes：两套主题各实例化一遍。
        //
        // 这是"换主题"这个功能唯一的自动化保障：调色板少一个键时，
        // DynamicResource 会在实例化那一刻抛 ResourceReferenceKeyNotFoundException，
        // 而编译期完全看不出来。所以必须两套都真的建一次窗口。
        if (e.Args.Any(a => string.Equals(a, "--selfcheck-themes", StringComparison.OrdinalIgnoreCase)))
        {
            var code = RunSelfCheckAllThemes();
            Log.Info($"---- 主题自检结束，退出码 {code} ----");
            Environment.Exit(code);
            return;
        }

        // --selfcheck-theme-switch：在**一个活着的窗口**上真的切一次主题。
        //
        // 前三项自检都只能证明"用某个主题建窗口不炸"，证明不了"换主题界面会跟着变色"。
        // 而后者恰恰是最容易悄悄失效的地方 —— 只要哪个属性当初写成了 StaticResource，
        // 换主题时它会继续显示旧颜色，且**不报任何错**。
        // 这里直接读元素的实际颜色，深色渲染一遍、切浅色再渲染一遍，颜色必须变。
        if (e.Args.Any(a => string.Equals(a, "--selfcheck-theme-switch", StringComparison.OrdinalIgnoreCase)))
        {
            var code = RunSelfCheckLiveThemeSwitch();
            Log.Info($"---- 实时主题切换自检结束，退出码 {code} ----");
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
    /// （Halo / HaloScale / Hover）并把它们设成动画的**终值**，数值与 Theme.xaml 里的 To 一致。
    /// 改 Theme.xaml 里的动画数值时，这里也要跟着改，否则截出来的不是真实效果。
    ///
    /// 想截浅色主题：加 --theme light（见 OnStartup），例如
    ///   0verClient.exe --theme light --screenshot build\ui-light.png
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

            // --page settings 可以截设置页 —— 主题选择栏在那里，
            // 它是自研模板的 ComboBox（默认 ComboBox 在浅色主题下会白字白底），
            // 必须真的渲染一次看一眼，自检只能证明"没抛异常"。
            var page = ArgValue(args, "--page");
            var showSettings = string.Equals(page, "settings", StringComparison.OrdinalIgnoreCase);
            if (showSettings)
                window.ViewModel.NavIndex = 2;

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

            // 设置页里没有卡片，这时候"没找到卡片"是正常情况，不算失败。
            var hovered = showSettings ? 0 : ForceHoverOnFirstCard(root);

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

            // 设置页没有卡片可悬停，不能用"悬停了 0 张"判定失败。
            return hovered > 0 || showSettings ? 0 : 1;
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

        // 悬停变亮是独立一层 Hover 的 Opacity 动画（见 Theme.xaml 的 GameCardButton）。
        // 数值必须和 Storyboard 里的 To 一致，否则截出来的不是真实效果。
        //
        // 这里刻意**不再**直接改 Body 的 Background：那样做等于绕过模板去伪造悬停态，
        // 结果是截图和真人看到的不是一回事（而且会把文字盖住也看不出来）。
        if (card.Template.FindName("Hover", card) is Border hover)
            hover.Opacity = 1;

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

    /// <summary>
    /// 两套主题各建一次主窗口。
    ///
    /// 单独存在的理由：换主题最容易犯的错是"某个颜色只写进了深色调色板"，
    /// 而这类错误只在**该主题被真正加载**时才炸（DynamicResource 找不到键会抛
    /// ResourceReferenceKeyNotFoundException）。只跑深色自检是发现不了的。
    /// </summary>
    private static int RunSelfCheckAllThemes()
    {
        var failed = 0;

        foreach (var kind in new[] { ThemeKind.Dark, ThemeKind.Light })
        {
            ThemeManager.Apply(kind);

            var name = ThemeManager.ToSettingValue(kind);
            var code = RunSelfCheck();

            if (code == 0)
            {
                Log.Info($"THEME SELFCHECK OK: {name}");
                Console.WriteLine($"主题 {name}: 通过");
            }
            else
            {
                Log.Error($"THEME SELFCHECK FAILED: {name}（退出码 {code}）");
                Console.WriteLine($"主题 {name}: 失败");
                failed++;
            }
        }

        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// 在一个真正布局过的窗口上切换主题，并断言颜色**真的变了**。
    ///
    /// 做法：往窗口的真实视觉树里插一个探针 Border，给它挂 SetResourceReference("WindowBg")
    /// —— 这正是 XAML 里写 {DynamicResource WindowBg} 时编译器生成的东西 ——
    /// 然后渲染、取色、切主题、再取色。颜色没变就说明重新求值链路断了（或者有人把它改回了
    /// StaticResource），退出码 1。
    ///
    /// 探针必须真的进视觉树。第一版把它建在树外面，结果切主题后颜色纹丝不动：
    /// 不在树上的元素拿不到资源失效通知，于是"测试失败"和"功能坏了"长得一模一样。
    /// </summary>
    private static int RunSelfCheckLiveThemeSwitch()
    {
        try
        {
            var window = new MainWindow();
            window.ViewModel.AddSampleCardForSelfCheck();

            if (window.Content is not UIElement root)
            {
                Log.Error("LIVE THEME SWITCH FAILED: MainWindow 没有内容");
                return 1;
            }

            // 探针铺满整个窗口最底层，既在视觉树里，又不会盖住别的东西。
            var probe = new Border();
            probe.SetResourceReference(Border.BackgroundProperty, "WindowBg");

            if (root is Panel panel)
                panel.Children.Insert(0, probe);
            else if (root is Decorator decorator && decorator.Child is Panel inner)
                inner.Children.Insert(0, probe);
            else
            {
                Log.Error($"LIVE THEME SWITCH FAILED: 根元素是 {root.GetType().Name}，没法插入探针");
                return 1;
            }

            root.Measure(new Size(1180, 720));
            root.Arrange(new Rect(0, 0, 1180, 720));
            root.UpdateLayout();

            var start = ThemeManager.Current;
            ThemeManager.Apply(ThemeKind.Dark);
            window.UpdateLayout();
            var before = ProbeColor(probe);

            ThemeManager.Apply(ThemeKind.Light);
            window.UpdateLayout();
            var afterLight = ProbeColor(probe);

            ThemeManager.Apply(ThemeKind.Dark);
            window.UpdateLayout();
            var afterDark = ProbeColor(probe);

            ThemeManager.Apply(start);

            Console.WriteLine($"深色窗口底色: {before}");
            Console.WriteLine($"浅色窗口底色: {afterLight}");
            Console.WriteLine($"切回深色:     {afterDark}");

            if (before == afterLight)
            {
                Log.Error($"LIVE THEME SWITCH FAILED: 切到浅色后底色仍是 {before}，DynamicResource 没有重新求值");
                return 1;
            }

            if (afterDark != before)
            {
                Log.Error($"LIVE THEME SWITCH FAILED: 切回深色得到 {afterDark}，与初始值 {before} 不一致");
                return 1;
            }

            Log.Info($"LIVE THEME SWITCH OK: {before} -> {afterLight} -> {afterDark}");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("LIVE THEME SWITCH FAILED", ex);
            return 1;
        }
    }

    private static string ProbeColor(Border probe) =>
        probe.Background is SolidColorBrush brush ? brush.Color.ToString() : "(非纯色画刷)";

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
