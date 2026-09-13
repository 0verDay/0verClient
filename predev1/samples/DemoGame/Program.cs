using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace OverClient.DemoGame;

/// <summary>
/// 示例"游戏"。它存在的唯一目的是证明启动器真的能从网络下载一个程序并把它跑起来。
/// 刻意不引用任何第三方库，也不依赖启动器 —— 它就是一款普通的独立游戏。
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var startedAt = DateTimeOffset.UtcNow;

        var app = new Application
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose
        };

        var title = new TextBlock
        {
            Text = "0verDay Demo Game",
            FontSize = 27,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var subtitle = new TextBlock
        {
            Text = "这个窗口是 0verClient 从网络上按 manifest 下载、校验 sha256 之后启动起来的。",
            FontSize = 12.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xB2)),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            MaxWidth = 430,
            Margin = new Thickness(0, 12, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var clock = new TextBlock
        {
            FontSize = 15,
            Foreground = new SolidColorBrush(Color.FromRgb(0x7F, 0xB0, 0xFF)),
            Margin = new Thickness(0, 22, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var arguments = new TextBlock
        {
            Text = args.Length == 0 ? "启动参数：无" : "启动参数：" + string.Join(" ", args),
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var quit = new Button
        {
            Content = "退出游戏",
            Width = 120,
            Height = 34,
            Margin = new Thickness(0, 26, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xFF)),
            BorderThickness = new Thickness(0),
            FontSize = 13,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        quit.Click += (_, _) => app.Shutdown();

        var panel = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        panel.Children.Add(title);
        panel.Children.Add(subtitle);
        panel.Children.Add(clock);
        panel.Children.Add(arguments);
        panel.Children.Add(quit);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.5) };
        timer.Tick += (_, _) =>
        {
            var elapsed = DateTimeOffset.UtcNow - startedAt;
            clock.Text = $"已运行 {elapsed.TotalSeconds:0.0} 秒";
        };
        timer.Start();

        var window = new Window
        {
            Title = "0verDay Demo Game",
            Width = 560,
            Height = 360,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = new SolidColorBrush(Color.FromRgb(0x10, 0x12, 0x18)),
            Content = panel
        };

        app.Run(window);
    }
}
