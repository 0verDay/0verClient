using OverClient.Core.Manifest;
using OverClient.Core.Util;

namespace OverClient.Core.Install;

public enum InstallPhase
{
    Planning,
    Downloading,
    Committing,
    Completed
}

/// <summary>安装进度快照。字节数是权威值，百分比由它推导。</summary>
public sealed record InstallProgress(
    InstallPhase Phase,
    string? CurrentFile,
    int FilesCompleted,
    int FilesTotal,
    long BytesCompleted,
    long BytesTotal,
    double BytesPerSecond,
    int DownloadedFiles = 0,
    int ReusedFiles = 0)
{
    public double Fraction =>
        BytesTotal > 0 ? Math.Clamp((double)BytesCompleted / BytesTotal, 0, 1)
        : FilesTotal > 0 ? Math.Clamp((double)FilesCompleted / FilesTotal, 0, 1)
        : 0;

    public int Percent => (int)Math.Round(Fraction * 100);

    public string PhaseText => Phase switch
    {
        InstallPhase.Planning => "准备中",
        InstallPhase.Downloading => "下载中",
        InstallPhase.Committing => "校验并写入",
        InstallPhase.Completed => "完成",
        _ => ""
    };

    public string DetailText
    {
        get
        {
            if (Phase == InstallPhase.Completed)
                return $"已安装 {Hashing.HumanBytes(BytesTotal)}";

            var parts = new List<string>
            {
                $"{FilesCompleted}/{FilesTotal} 个文件",
                $"{Hashing.HumanBytes(BytesCompleted)} / {Hashing.HumanBytes(BytesTotal)}"
            };

            if (BytesPerSecond > 0 && Phase == InstallPhase.Downloading)
                parts.Add(Hashing.HumanSpeed(BytesPerSecond));

            if (ReusedFiles > 0)
                parts.Add($"复用 {ReusedFiles} 个");

            return string.Join(" · ", parts);
        }
    }

    public string EtaText
    {
        get
        {
            if (Phase != InstallPhase.Downloading || BytesPerSecond <= 0)
                return "";

            var remaining = BytesTotal - BytesCompleted;
            if (remaining <= 0)
                return "";

            return "剩余 " + Hashing.HumanEta(TimeSpan.FromSeconds(remaining / BytesPerSecond));
        }
    }
}

public sealed record InstallResult(
    string GameId,
    string Name,
    string Version,
    string Channel,
    string InstallDir,
    string Executable,
    List<string> Arguments,
    long TotalBytes,
    int DownloadedFiles,
    int ReusedFiles,
    string? ManifestSha256 = null);

/// <summary>
/// 安装引擎。核心承诺：
/// 1) 一切先落 staging，全部校验通过后才原子切换 —— 失败绝不破坏现有安装；
/// 2) 已存在且哈希正确的文件用硬链接复用，增量安装接近瞬时；
/// 3) manifest 里的路径在碰磁盘之前就已通过安全校验。
/// </summary>
public sealed class Installer(Net.IContentSource source)
{
    private readonly Net.IContentSource _source = source;

    public async Task<InstallResult> InstallAsync(
        GameManifest manifest,
        string gamesRoot,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(gamesRoot);

        var targetDir = Path.Combine(gamesRoot, SafePath.SanitizeSegment(manifest.GameId));
        var staging = Path.Combine(gamesRoot, $".staging-{SafePath.SanitizeSegment(manifest.GameId)}-{Guid.NewGuid():N}");

        var filesTotal = manifest.Files.Count;
        var bytesTotal = manifest.TotalBytes;
        var bytesCompleted = 0L;
        var filesCompleted = 0;
        var downloaded = 0;
        var reused = 0;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        progress?.Report(new InstallProgress(InstallPhase.Planning, null, 0, filesTotal, 0, bytesTotal, 0));
        Directory.CreateDirectory(staging);

        void Report(string? currentFile, InstallPhase phase)
        {
            var speed = stopwatch.Elapsed.TotalSeconds > 0.25
                ? bytesCompleted / stopwatch.Elapsed.TotalSeconds
                : 0;
            progress?.Report(new InstallProgress(
                phase, currentFile, filesCompleted, filesTotal, bytesCompleted, bytesTotal, speed, downloaded, reused));
        }

        try
        {
            foreach (var file in manifest.Files)
            {
                ct.ThrowIfCancellationRequested();

                var destination = SafePath.Resolve(staging, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

                var existing = SafePath.Resolve(targetDir, file.Path);
                var baseline = bytesCompleted;

                if (await TryReuseAsync(existing, file, destination, ct).ConfigureAwait(false))
                {
                    reused++;
                }
                else
                {
                    var captured = file;
                    var reporter = new Progress<long>(current => progress?.Report(new InstallProgress(
                        InstallPhase.Downloading,
                        captured.Path,
                        filesCompleted,
                        filesTotal,
                        baseline + current,
                        bytesTotal,
                        stopwatch.Elapsed.TotalSeconds > 0.25 ? bytesCompleted / stopwatch.Elapsed.TotalSeconds : 0,
                        downloaded,
                        reused)));

                    await _source.DownloadFileAsync(
                        file.ResolveUrl(manifest.BaseUrl), destination, file.Size, file.Sha256, reporter, ct)
                        .ConfigureAwait(false);

                    downloaded++;
                }

                bytesCompleted = baseline + file.Size;
                filesCompleted++;
                Report(file.Path, InstallPhase.Downloading);
            }

            Report(null, InstallPhase.Committing);

            // 原子切换：先把旧目录挪走，再把 staging 就位，最后清理旧目录。
            var trash = $"{targetDir}.old-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
            if (Directory.Exists(targetDir))
                Directory.Move(targetDir, trash);

            Directory.Move(staging, targetDir);

            if (Directory.Exists(trash))
                DeleteDirectoryWithRetry(trash);

            stopwatch.Stop();
            Log.Info($"安装完成 {manifest.GameId} {manifest.Version} -> {targetDir}（下载 {downloaded} / 复用 {reused}）");

            Report(null, InstallPhase.Completed);

            return new InstallResult(
                manifest.GameId,
                manifest.Name,
                manifest.Version,
                manifest.Channel,
                targetDir,
                manifest.Launch.Executable,
                [.. manifest.Launch.Arguments],
                bytesTotal,
                downloaded,
                reused,
                manifest.ManifestSha256);
        }
        catch
        {
            // 半成品绝不允许留在磁盘上；现有安装因为我们从未原地修改过，依然可用。
            DeleteDirectoryWithRetry(staging);
            throw;
        }
    }

    /// <summary>已存在且哈希正确的文件 → 硬链接进 staging（失败则复制）。</summary>
    private static async Task<bool> TryReuseAsync(string existingPath, FileEntry file, string destination, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(existingPath))
                return false;

            // 快路径：大小不符直接判定不可复用，省掉一次全文件哈希。
            if (new FileInfo(existingPath).Length != file.Size)
                return false;

            var hash = await Hashing.Sha256FileAsync(existingPath, ct).ConfigureAwait(false);
            if (!Hashing.Equals(hash, file.Sha256))
                return false;

            if (HardLink.TryCreate(destination, existingPath))
                return true;

            File.Copy(existingPath, destination, overwrite: true);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"复用已存在文件失败，将重新下载 {file.Path}：{ex.Message}");
            return false;
        }
    }

    /// <summary>游戏进程可能仍占着文件句柄，删除要重试而不是直接失败。</summary>
    internal static void DeleteDirectoryWithRetry(string path, int attempts = 5)
    {
        for (var i = 0; i < attempts; i++)
        {
            try
            {
                if (!Directory.Exists(path))
                    return;

                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (i == attempts - 1)
                {
                    Log.Warn($"清理目录失败（已重试 {attempts} 次）：{path} :: {ex.Message}");
                    return;
                }

                Thread.Sleep(150 * (i + 1));
            }
        }
    }
}
