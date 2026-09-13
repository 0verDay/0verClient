using System.Diagnostics;
using OverClient.Core.Install;
using OverClient.Core.Util;

namespace OverClient.Core.Launch;

public sealed class GameExitEventArgs(string gameId, int exitCode, TimeSpan duration) : EventArgs
{
    public string GameId { get; } = gameId;
    public int ExitCode { get; } = exitCode;
    public TimeSpan Duration { get; } = duration;

    /// <summary>秒退且非零退出码 = 崩溃（独立游戏最常见的故障形态）。</summary>
    public bool Crashed { get; } = exitCode != 0 && duration.TotalSeconds < 30;
}

public sealed class RunningGame
{
    public string GameId { get; init; } = "";
    public int ProcessId { get; init; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    internal Process Process { get; init; } = null!;
}

/// <summary>
/// 进程守护。
/// 设计取舍：游戏进程默认**不**随启动器退出而结束（和 Steam 一致），
/// 因此这里不把游戏塞进 Job Object —— 那会在启动器退出时连带杀掉游戏。
/// 将来用于 7z/安装辅助进程时才会引入 Job Object 做树级回收。
/// </summary>
public sealed class GameLauncher : IDisposable
{
    private readonly Dictionary<int, RunningGame> _running = [];
    private readonly Lock _gate = new();

    private bool _disposed;

    public event EventHandler<GameExitEventArgs>? GameExited;

    /// <summary>是否在启动器退出时结束所有游戏。默认 false。</summary>
    public bool KillGamesOnExit { get; set; }

    public IReadOnlyList<RunningGame> Running
    {
        get
        {
            lock (_gate)
                return [.. _running.Values];
        }
    }

    public bool IsRunning(string gameId)
    {
        lock (_gate)
            return _running.Values.Any(r => string.Equals(r.GameId, gameId, StringComparison.OrdinalIgnoreCase));
    }

    public RunningGame Launch(InstalledGame game)
    {
        if (IsRunning(game.GameId))
            throw new InvalidOperationException($"游戏 {game.Name} 已经在运行");

        var executable = SafePath.Resolve(game.InstallDir, game.Executable);
        if (!File.Exists(executable))
            throw new FileNotFoundException($"找不到可执行文件：{executable}", executable);

        var workingDirectory = Path.GetDirectoryName(executable)!;
        var isCommandScript = IsCommandScript(executable);

        // .cmd / .bat 无法被 CreateProcess 直接执行，必须经由命令解释器。
        // 用 .bat 当入口的独立游戏并不少见，所以这是一个真实分支，不是为测试硬加的。
        var startInfo = new ProcessStartInfo
        {
            FileName = isCommandScript
                ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe"
                : executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = false
        };

        if (isCommandScript)
        {
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(executable);
        }

        // 参数一律走 ArgumentList，绝不拼命令行字符串 —— 中文/空格路径会炸。
        foreach (var argument in game.Arguments)
            startInfo.ArgumentList.Add(argument);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        if (!process.Start())
            throw new InvalidOperationException($"启动失败：{executable}");

        var running = new RunningGame
        {
            GameId = game.GameId,
            ProcessId = process.Id,
            StartedAt = DateTimeOffset.UtcNow,
            Process = process
        };

        lock (_gate)
            _running[process.Id] = running;

        process.Exited += (_, _) => HandleExit(running);

        Log.Info($"启动游戏 {game.Name} pid={process.Id} exe={executable}");
        return running;
    }

    private void HandleExit(RunningGame running)
    {
        var duration = DateTimeOffset.UtcNow - running.StartedAt;
        int exitCode;

        try
        {
            exitCode = running.Process.ExitCode;
        }
        catch (Exception ex)
        {
            Log.Warn($"读取退出码失败：{ex.Message}");
            exitCode = -1;
        }

        lock (_gate)
            _running.Remove(running.ProcessId);

        Log.Info($"游戏退出 {running.GameId} pid={running.ProcessId} code={exitCode} 时长={duration.TotalSeconds:0.0}s");

        try
        {
            GameExited?.Invoke(this, new GameExitEventArgs(running.GameId, exitCode, duration));
        }
        catch (Exception ex)
        {
            Log.Error("处理游戏退出事件时出错", ex);
        }

        try
        {
            running.Process.Dispose();
        }
        catch
        {
            // 忽略
        }
    }

    private static bool IsCommandScript(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>结束游戏（含它派生的子进程）。</summary>
    public bool Kill(string gameId)
    {
        RunningGame? target;
        lock (_gate)
            target = _running.Values.FirstOrDefault(r => string.Equals(r.GameId, gameId, StringComparison.OrdinalIgnoreCase));

        if (target is null)
            return false;

        try
        {
            target.Process.Kill(entireProcessTree: true);
            Log.Info($"已结束游戏 {gameId} pid={target.ProcessId}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"结束游戏失败 {gameId}：{ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (!KillGamesOnExit)
            return;

        foreach (var running in Running)
        {
            try
            {
                running.Process.Kill(entireProcessTree: true);
            }
            catch
            {
                // 忽略
            }
        }
    }
}
