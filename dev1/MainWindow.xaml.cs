using System.Windows;
using System.Windows.Input;

namespace _0verClient;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void MainBorder_MouseEnter(object sender, MouseEventArgs e)
    {
        mainBorder.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.White);
    }

    private void MainBorder_MouseLeave(object sender, MouseEventArgs e)
    {
        mainBorder.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(68, 68, 68));
    }
}
