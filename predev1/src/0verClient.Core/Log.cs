using System.Globalization;
using OverClient.Core.Util;

namespace OverClient.Core;

/// <summary>最简结构化日志：按天一个文件 + 事件流（UI 可直接订阅显示）。</summary>
public static class Log
{
    private static readonly Lock Gate = new();
    private static string? _filePath;
    private static bool _initialized;

    public static event Action<string>? LineWritten;

    public static void Init(string? logDirectory = null)
    {
        lock (Gate)
        {
            if (_initialized)
                return;

            try
            {
                var dir = logDirectory ?? AppPaths.Logs;
                Directory.CreateDirectory(dir);
                _filePath = Path.Combine(dir, $"0verclient-{DateTime.Now:yyyyMMdd}.log");
                _initialized = true;
                Info($"==== {AppInfo.Name} {AppInfo.Version} 启动，日志写入 {_filePath} ====");
            }
            catch
            {
                // 日志失败绝不能拖垮启动器。
                _initialized = true;
            }
        }
    }

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? ex = null)
    {
        if (ex is null)
        {
            Write("ERROR", message);
            return;
        }

        Write("ERROR", $"{message} :: {ex.GetType().Name}: {ex.Message}");

        // 只有类型和消息是不够的：定位一次 XamlParseException 能多花十几分钟。
        // ex.ToString() 带堆栈和内部异常，必须留在日志里。
        Write("ERROR", ex.ToString());
    }

    private static void Write(string level, string message)
    {
        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}");

        lock (Gate)
        {
            if (_filePath is not null)
            {
                try
                {
                    File.AppendAllText(_filePath, line + Environment.NewLine);
                }
                catch
                {
                    // 故意吞掉：磁盘满/被占用不应该影响游戏下载。
                }
            }
        }

        try
        {
            LineWritten?.Invoke(line);
        }
        catch
        {
            // 订阅者自己炸了不关我们的事。
        }
    }
}
