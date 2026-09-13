using System.Windows;
using System.Windows.Input;
using OverClient.App.ViewModels;
using OverClient.Core;

namespace OverClient.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    /// <summary>自检需要往里面塞一张卡片，好让卡片模板真的被实例化一次。</summary>
    internal MainViewModel ViewModel => _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        Loaded += async (_, _) =>
        {
            try
            {
                await _viewModel.InitializeAsync();
            }
            catch (Exception ex)
            {
                Log.Error("初始化失败", ex);
            }
        };
    }

    private void TopBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 鼠标已经抬起，忽略即可。
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}
