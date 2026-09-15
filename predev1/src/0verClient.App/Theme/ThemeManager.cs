using System.Windows;
using OverClient.Core;

namespace OverClient.App.Theme;

public enum ThemeKind
{
    Dark,
    Light
}

/// <summary>
/// 主题切换。
///
/// 实现方式：Application.Resources 的合并字典里固定挂一份 Theme.xaml（样式），
/// 换主题就是把它旁边那份调色板换成另一份。
///
/// 为什么能"实时"生效：Theme.xaml / MainWindow.xaml 里所有颜色都是 DynamicResource，
/// 它们挂的是**键**而不是画刷实例，调色板一换，WPF 立刻按新键值重新求值。
/// 如果当初用的是 StaticResource，换主题不会有任何反应（值在解析 XAML 时就写死了）。
///
/// 顺序要求：必须在创建 MainWindow **之前**调用 Apply，这样窗口第一次解析 XAML 时
/// 就能取到正确主题的颜色，不会先闪一下深色再变浅色。
/// </summary>
public static class ThemeManager
{
    private const string ThemeDictionaryPath = "Theme/Theme.xaml";
    private const string DarkPalettePath = "Theme/Palette.Dark.xaml";
    private const string LightPalettePath = "Theme/Palette.Light.xaml";

    /// <summary>当前已挂载的主题。默认深色，与 App.xaml 里声明的初始合并字典一致。</summary>
    public static ThemeKind Current { get; private set; } = ThemeKind.Dark;

    public static bool IsLight => Current == ThemeKind.Light;

    /// <summary>主题切换后触发，供界面刷新主题相关的文案（例如设置页的说明）。</summary>
    public static event EventHandler? Changed;

    public static ThemeKind Parse(string? value) =>
        string.Equals(value?.Trim(), "light", StringComparison.OrdinalIgnoreCase)
            ? ThemeKind.Light
            : ThemeKind.Dark;

    public static string ToSettingValue(ThemeKind kind) => kind == ThemeKind.Light ? "light" : "dark";

    /// <summary>把用户在设置里选的 "dark"/"light" 文本规范成合法值。</summary>
    public static string NormalizeSetting(string? value) => ToSettingValue(Parse(value));

    public static void Apply(ThemeKind kind)
    {
        var app = Application.Current;
        if (app is null)
        {
            // 没有 Application 就没有资源字典可换（理论上只在单测/工具路径里发生）。
            Current = kind;
            return;
        }

        var dictionaries = app.Resources.MergedDictionaries;
        var palettePath = kind == ThemeKind.Light ? LightPalettePath : DarkPalettePath;

        try
        {
            var theme = Load(ThemeDictionaryPath);
            var palette = Load(palettePath);

            // 整体替换（而不是"删掉旧调色板再插新的"）：Theme.xaml 里那些
            // BasedOn="{StaticResource ...}" 依赖样式字典先于调色板可用，
            // 一次性换掉可以避免中间态出现"找不到资源"的窗口。
            dictionaries.Clear();
            dictionaries.Add(theme);
            dictionaries.Add(palette);

            Current = kind;
            Log.Info($"主题已切换为 {ToSettingValue(kind)}");
        }
        catch (Exception ex)
        {
            Log.Error($"主题切换失败（{palettePath}），保持原主题", ex);
            return;
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void Apply(string? settingValue) => Apply(Parse(settingValue));

    /// <summary>
    /// 依次应用每个主题。
    /// 自检/截图用：一个进程里把两套调色板都真正加载并渲染一遍，
    /// 才能证明"浅色主题不缺资源"（缺键会抛 ResourceReferenceKeyNotFoundException）。
    /// </summary>
    public static void ApplyAllForSelfCheck()
    {
        Apply(ThemeKind.Dark);
        Apply(ThemeKind.Light);
    }

    private static ResourceDictionary Load(string relativePath)
    {
        // 相对 URI 是 WPF 认的标准写法（与 App.xaml 里 <ResourceDictionary Source="Theme/Theme.xaml" />
        // 完全等价），会解析到本程序集里编译进去的 .baml。
        // 单文件发布（PublishSingleFile）下同样成立 —— 资源在程序集里，不在磁盘上。
        return new ResourceDictionary { Source = new Uri(relativePath, UriKind.Relative) };
    }
}
