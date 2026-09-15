using System.Diagnostics;
using OverClient.Core.Net;
using OverClient.Core.Util;

namespace OverClient.Core.Update;

public sealed record LauncherDownloadProgress(long BytesReceived, long BytesTotal)
{
    public double Fraction =>
        BytesTotal > 0 ? Math.Clamp((double)BytesReceived / BytesTotal, 0, 1) : 0;

    public int Percent => (int)Math.Round(Fraction * 100);

    public string DetailText => BytesTotal > 0
        ? $"{Hashing.HumanBytes(BytesReceived)} / {Hashing.HumanBytes(BytesTotal)}"
        : Hashing.HumanBytes(BytesReceived);
}

/// <summary>
/// 启动器自更新。
///
/// 核心约束：Windows 不允许覆盖一个**正在运行**的 exe。所以流程是"新版自己当更新器"：
///
///   1) 旧版把新 exe 下载到 %LOCALAPPDATA%\0verClient\update\ 并校验 sha256；
///   2) 旧版用 <c>--apply-update &lt;目标路径&gt; &lt;旧版PID&gt;</c> 启动**新 exe**，然后自己退出；
///   3) 新 exe 在更新模式下等目标文件不再被占用，把自己复制到目标路径，再正常启动目标。
///
/// 为什么不用 .cmd 批处理来搬运：批处理里的路径要经过控制台代码页编码，
/// 用户名或安装路径里只要有中文就会乱码 —— 本仓库的 run-demo.ps1 已经踩过同一个坑
/// （见 README 的"三条编辑规矩"）。走 CreateProcessW 传参数，路径是 UTF-16，没有这个问题。
///
/// 读自己再写别人也是安全的：Windows 只锁运行中映像的**写入**，读取（复制）不受限。
/// </summary>
public static class LauncherUpdater
{
    public const string ApplyFlag = "--apply-update";

    /// <summary>下载与替换都用这个目录，方便用户出问题时手动找新版。</summary>
    public static string StagingDirectory => Path.Combine(AppPaths.Root, "update");

    /// <summary>当前正在运行的启动器 exe 完整路径（单文件发布下也是真实路径，不是解包临时目录）。</summary>
    public static string CurrentExecutablePath
    {
        get
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(path))
                return Path.GetFullPath(path);

            var module = Process.GetCurrentProcess().MainModule?.FileName;
            return Path.GetFullPath(string.IsNullOrWhiteSpace(module) ? "0verClient.exe" : module);
        }
    }

    public static string TargetPathFor(string version) =>
        Path.Combine(StagingDirectory, $"0verClient-{SanitizeVersion(version)}.exe");

    /// <summary>下载并校验新版启动器，返回它在磁盘上的路径。校验不过会抛异常。</summary>
    public static async Task<string> DownloadAsync(
        IContentSource source,
        LauncherUpdateDecision decision,
        IProgress<LauncherDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!decision.CanAutoApply)
            throw new InvalidOperationException("这次更新没有提供下载地址或 sha256，无法自动更新。");

        Directory.CreateDirectory(StagingDirectory);
        CleanupStaging(keep: null);

        var target = TargetPathFor(decision.Version);

        var reporter = progress is null
            ? null
            : new Progress<long>(bytes => progress.Report(new LauncherDownloadProgress(bytes, decision.Size)));

        await source
            .DownloadFileAsync(decision.Url!, target, decision.Size, decision.Sha256!, reporter, ct)
            .ConfigureAwait(false);

        Log.Info($"新版启动器已下载并校验：{target}");
        return target;
    }

    /// <summary>
    /// 把接力棒交给新版：启动它去完成替换，调用方**必须**紧接着退出，
    /// 否则目标 exe 一直被占用，新版会等到超时。
    /// </summary>
    public static void HandOver(string stagedExe, int currentProcessId)
    {
        var info = new ProcessStartInfo
        {
            FileName = stagedExe,
            Arguments = $"{ApplyFlag} \"{CurrentExecutablePath}\" {currentProcessId}",
            UseShellExecute = false,
            WorkingDirectory = StagingDirectory
        };

        Process.Start(info);
        Log.Info($"已启动新版 {stagedExe} 接管更新（父进程 {currentProcessId} 即将退出）");
    }

    /// <summary>解析 <c>--apply-update &lt;目标&gt; &lt;PID&gt;</c>；不是更新模式时返回 null。</summary>
    public static (string TargetExe, int ParentPid)? ParseApplyArgs(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (!string.Equals(args[i], ApplyFlag, StringComparison.OrdinalIgnoreCase))
                continue;

            if (i + 2 >= args.Count)
                return null;

            return int.TryParse(args[i + 2], out var pid) ? (args[i + 1], pid) : null;
        }

        return null;
    }

    /// <summary>
    /// 更新模式的主逻辑：等目标不再被占用 → 把自己覆盖到目标路径 → 启动目标。
    /// 返回进程退出码，0 表示成功。
    /// </summary>
    public static int ApplyUpdate(string targetExe, int parentPid, TimeSpan? waitFor = null)
    {
        var timeout = waitFor ?? TimeSpan.FromSeconds(60);
        var self = CurrentExecutablePath;

        Log.Info($"更新模式启动：新版 {self} 等待替换 {targetExe}（旧进程 {parentPid}）");

        if (!WaitUntilReplaceable(targetExe, timeout))
        {
            Log.Error($"更新失败：{timeout.TotalSeconds:0} 秒内 {targetExe} 仍被占用或不可写。新版留在 {self}");
            return 1;
        }

        if (!SafePath.IsSamePath(self, targetExe) && !CopyWithRetry(self, targetExe))
        {
            Log.Error($"更新失败：无法覆盖 {targetExe}。新版留在 {self}");
            return 1;
        }

        Log.Info($"更新完成，启动 {targetExe}");
        StartDetached(targetExe);
        return 0;
    }

    /// <summary>
    /// 用"能不能独占打开"来判断目标是否可以替换。
    ///
    /// 比轮询进程句柄可靠得多：进程句柄会受权限/沙箱影响（打不开就误判），
    /// 而文件占用状态是 Windows 自己维护的，直接对应"现在能不能写这个文件"。
    /// </summary>
    private static bool WaitUntilReplaceable(string targetExe, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (true)
        {
            try
            {
                using var _ = new FileStream(targetExe, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 仍在被占用（或暂时不可写）—— 继续等。
            }

            if (DateTime.UtcNow >= deadline)
                return false;

            Thread.Sleep(200);
        }
    }

    private static bool CopyWithRetry(string source, string destination, int attempts = 10)
    {
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                var directory = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                File.Copy(source, destination, overwrite: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"覆盖 {destination} 失败（第 {attempt}/{attempts} 次）：{ex.Message}");

                if (attempt == attempts)
                    return false;

                Thread.Sleep(300 * attempt);
            }
        }

        return false;
    }

    private static void StartDetached(string exePath)
    {
        try
        {
            var directory = Path.GetDirectoryName(exePath);
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                WorkingDirectory = string.IsNullOrEmpty(directory) ? AppPaths.Root : directory
            });
        }
        catch (Exception ex)
        {
            Log.Error($"启动 {exePath} 失败", ex);
        }
    }

    /// <summary>清掉上一次更新留下的旧版本文件，避免 update 目录越攒越大。</summary>
    private static void CleanupStaging(string? keep)
    {
        try
        {
            if (!Directory.Exists(StagingDirectory))
                return;

            foreach (var file in Directory.EnumerateFiles(StagingDirectory, "0verClient-*.exe"))
            {
                if (keep is not null && SafePath.IsSamePath(file, keep))
                    continue;

                try
                {
                    File.Delete(file);
                }
                catch (Exception ex)
                {
                    Log.Warn($"清理旧更新文件失败 {file}：{ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"清理更新目录失败：{ex.Message}");
        }
    }

    private static string SanitizeVersion(string version)
    {
        var safe = new string([.. version.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_')]);
        return string.IsNullOrEmpty(safe) ? "new" : safe;
    }
}
