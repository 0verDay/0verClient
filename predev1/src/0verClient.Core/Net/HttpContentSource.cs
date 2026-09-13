using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using OverClient.Core.Util;

namespace OverClient.Core.Net;

public interface IContentSource
{
    Task<byte[]> GetBytesAsync(string url, CancellationToken ct = default);
    Task<string> GetTextAsync(string url, CancellationToken ct = default);

    /// <summary>下载单个文件到 destinationPath，带断点续传、重试与 sha256 校验。</summary>
    Task DownloadFileAsync(
        string url,
        string destinationPath,
        long expectedSize,
        string expectedSha256,
        IProgress<long>? bytesProgress = null,
        CancellationToken ct = default);
}

/// <summary>
/// 纯 HttpClient 实现。刻意不引入任何第三方库：
/// 断点续传只需要一个 Range 头，重试只需要一层退避循环。
/// </summary>
public sealed class HttpContentSource : IContentSource, IDisposable
{
    private const int MaxAttempts = 4;
    private const int BufferSize = 1 << 20;
    private const long ProgressGranularity = 512 * 1024;

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public HttpContentSource(HttpClient? httpClient = null)
    {
        _ownsHttp = httpClient is null;
        _http = httpClient ?? CreateDefaultClient();
    }

    public static HttpClient CreateDefaultClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 16
        };

        var client = new HttpClient(handler)
        {
            // 大文件下载由每个请求自己控制超时，整体超时必须关掉。
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.UserAgent);
        return client;
    }

    public async Task<byte[]> GetBytesAsync(string url, CancellationToken ct = default) =>
        await _http.GetByteArrayAsync(url, ct).ConfigureAwait(false);

    public async Task<string> GetTextAsync(string url, CancellationToken ct = default) =>
        await _http.GetStringAsync(url, ct).ConfigureAwait(false);

    public async Task DownloadFileAsync(
        string url,
        string destinationPath,
        long expectedSize,
        string expectedSha256,
        IProgress<long>? bytesProgress = null,
        CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var partPath = destinationPath + ".part";
        var stopwatch = Stopwatch.StartNew();
        Exception? lastError = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await DownloadOnceAsync(url, partPath, bytesProgress, ct).ConfigureAwait(false);

                if (expectedSize > 0)
                {
                    var actualSize = new FileInfo(partPath).Length;
                    if (actualSize != expectedSize)
                        throw new InvalidDataException(
                            $"大小不符：期望 {expectedSize} 字节，实际 {actualSize} 字节（{url}）");
                }

                var actualHash = await Hashing.Sha256FileAsync(partPath, ct).ConfigureAwait(false);
                if (!Hashing.Equals(actualHash, expectedSha256))
                {
                    // 内容不对，续传的 .part 已经不可信，删掉重来。
                    TryDelete(partPath);
                    throw new InvalidDataException(
                        $"sha256 校验失败：期望 {expectedSha256}，实际 {actualHash}（{url}）");
                }

                if (File.Exists(destinationPath))
                    File.Delete(destinationPath);

                File.Move(partPath, destinationPath);
                Log.Info($"下载完成 {Path.GetFileName(destinationPath)} ({Hashing.HumanBytes(expectedSize)}) 用时 {stopwatch.Elapsed.TotalSeconds:0.0}s");
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (attempt == MaxAttempts)
                    break;

                var delay = TimeSpan.FromMilliseconds(400 * Math.Pow(2, attempt - 1));
                Log.Warn($"下载失败（第 {attempt}/{MaxAttempts} 次），{delay.TotalMilliseconds:0}ms 后重试：{ex.Message}");
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }

        throw new IOException($"下载失败（已重试 {MaxAttempts} 次）：{url}", lastError);
    }

    private async Task DownloadOnceAsync(
        string url,
        string partPath,
        IProgress<long>? bytesProgress,
        CancellationToken ct)
    {
        long alreadyHave = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (alreadyHave > 0)
            request.Headers.Range = new RangeHeaderValue(alreadyHave, null);

        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        var resumed = alreadyHave > 0 && response.StatusCode == HttpStatusCode.PartialContent;

        if (!resumed)
        {
            // 服务器不支持 Range（或我们本来就没有 .part），从零开始。
            alreadyHave = 0;
            TryDelete(partPath);
            response.EnsureSuccessStatusCode();
        }

        var mode = resumed ? FileMode.Append : FileMode.Create;

        await using (var file = new FileStream(
            partPath, mode, FileAccess.Write, FileShare.None,
            bufferSize: BufferSize, FileOptions.Asynchronous))
        await using (var network = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        {
            var buffer = new byte[BufferSize];
            long total = alreadyHave;
            long lastReported = alreadyHave;
            bytesProgress?.Report(total);

            int read;
            while ((read = await network.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                total += read;

                // 进度事件必须节流：几万个分片不节流会把 UI 线程打爆。
                if (total - lastReported >= ProgressGranularity)
                {
                    lastReported = total;
                    bytesProgress?.Report(total);
                }
            }

            await file.FlushAsync(ct).ConfigureAwait(false);
            bytesProgress?.Report(total);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"删除临时文件失败 {path}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }
}
