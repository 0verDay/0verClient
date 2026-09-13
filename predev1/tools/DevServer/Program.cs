using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using OverClient.Core.Manifest;
using OverClient.Core.Util;

namespace OverClient.DevServer;

/// <summary>
/// 本地静态内容源。
/// 它扮演的角色和你将来真正要用的对象存储 / CDN 完全一样：
/// 只提供三个东西 —— index.json、manifest.json、以及带 Range 支持的文件。
/// 刻意用裸 TcpListener 而不是 HttpListener，因为 HttpListener 在 Windows 上
/// 绑定 localhost 需要管理员预留 URL ACL，裸 socket 不需要。
/// </summary>
internal static class Program
{
    private const string GameId = "demo-sandbox";
    private const string GameVersion = "1.0.0";

    private static string _gameRoot = "";
    private static string? _siteRoot;
    private static byte[] _indexBytes = [];
    private static byte[] _manifestBytes = [];
    private static long _throttleBytesPerSecond;
    private static long _dropAfter;
    private static int _dropsRemaining;

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var port = GetInt(args, "--port", 8787);
        _throttleBytesPerSecond = (long)(GetDouble(args, "--throttle-kbps", 512) * 1024);
        _dropAfter = GetInt(args, "--drop-after", 0);
        _dropsRemaining = _dropAfter > 0 ? 1 : 0;

        // 两种模式：
        //   --site <dir>  直接托管一个由 tools/Publish 生成的静态站点（和 nginx 的行为一致）
        //   --root <dir>  现场从游戏目录生成清单（跑内置 demo 用）
        _siteRoot = GetString(args, "--site");

        if (_siteRoot is not null)
        {
            if (!Directory.Exists(_siteRoot))
            {
                Console.Error.WriteLine($"站点目录不存在：{_siteRoot}");
                return 1;
            }

            _siteRoot = Path.GetFullPath(_siteRoot);
        }
        else
        {
            _gameRoot = GetString(args, "--root") ?? FindDemoGameOutput() ?? "";

            if (string.IsNullOrWhiteSpace(_gameRoot) || !Directory.Exists(_gameRoot))
            {
                Console.Error.WriteLine("找不到示例游戏输出目录。");
                Console.Error.WriteLine();
                Console.Error.WriteLine("用法: DevServer [--root <游戏目录> | --site <静态站点目录>] [--port 8787] [--throttle-kbps 512] [--drop-after 262144]");
                Console.Error.WriteLine();
                Console.Error.WriteLine("--root 模式先编译示例游戏，让下面这个目录存在：");
                Console.Error.WriteLine("  samples/DemoGame/bin/Debug/net10.0-windows/");
                Console.Error.WriteLine("--site 模式先跑 tools/Publish 生成站点目录。");
                return 1;
            }

            BuildContent(port);
        }

        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();

        if (_siteRoot is not null)
        {
            Console.WriteLine($"内容源已启动（静态站点模式，目录 {_siteRoot}）");
            Console.WriteLine($"  索引    http://127.0.0.1:{port}/index.json");
        }
        else
        {
            Console.WriteLine($"内容源已启动（游戏文件来自 {_gameRoot}）");
            Console.WriteLine($"  索引    http://127.0.0.1:{port}/index.json");
            Console.WriteLine($"  清单    http://127.0.0.1:{port}/games/{GameId}/manifest.json");
        }

        Console.WriteLine($"  限速    {(_throttleBytesPerSecond > 0 ? $"{_throttleBytesPerSecond / 1024} KB/s" : "不限速")}（--throttle-kbps 0 关闭）");
        if (_dropAfter > 0)
            Console.WriteLine($"  断线演练 首条连接将在 {_dropAfter} 字节后掐断（用来验证断点续传）");
        Console.WriteLine();
        Console.WriteLine("按 Ctrl+C 停止。");

        while (true)
        {
            var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private static void BuildContent(int port)
    {
        var baseUrl = $"http://127.0.0.1:{port}/games/{GameId}/files/";
        var files = new List<FileEntry>();

        foreach (var path in Directory.EnumerateFiles(_gameRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(_gameRoot, path).Replace('\\', '/');

            // 符号调试文件不参与分发。
            if (relative.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
                continue;

            var info = new FileInfo(path);
            files.Add(new FileEntry
            {
                Path = relative,
                Size = info.Length,
                Sha256 = Hashing.Sha256FileAsync(path).GetAwaiter().GetResult(),
                Executable = relative.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            });
        }

        if (files.Count == 0)
            throw new InvalidOperationException($"{_gameRoot} 里没有任何文件，先编译示例游戏。");

        files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));

        var executable = files.FirstOrDefault(f => f.Executable)
            ?? throw new InvalidOperationException($"{_gameRoot} 里找不到 .exe，示例游戏没有编译成功？");

        var manifest = new GameManifest
        {
            SchemaVersion = GameManifest.CurrentSchema,
            GameId = GameId,
            Name = "0verDay Demo",
            Version = GameVersion,
            Channel = "latest",
            PublishedAt = DateTimeOffset.UtcNow,
            ReleaseNotes = "首个演示版本：验证 manifest 驱动的下载、校验与启动链路。",
            LauncherMinVersion = "0.1.0",
            BaseUrl = baseUrl,
            Files = files,
            Launch = new LaunchSpec
            {
                Executable = executable.Path,
                Arguments = [],
                WorkingDirectory = "."
            }
        };

        _manifestBytes = Encoding.UTF8.GetBytes(JsonDefaults.Serialize(manifest));

        var index = new RootIndex
        {
            SchemaVersion = RootIndex.CurrentSchema,
            GeneratedAt = DateTimeOffset.UtcNow,
            Games =
            [
                new GameEntry
                {
                    Id = GameId,
                    Name = "0verDay Demo",
                    Summary = "启动器自己下载并启动的示例程序，用来验证整条安装链路。",
                    AccentColor = "#4C8DFF",
                    Tags = ["demo", "wpf", "离线可玩"],
                    Channels =
                    {
                        ["latest"] = new ChannelEntry
                        {
                            Version = GameVersion,
                            ManifestUrl = $"http://127.0.0.1:{port}/games/{GameId}/manifest.json",
                            ManifestSha256 = Hashing.Sha256Bytes(_manifestBytes),
                            ManifestSize = _manifestBytes.Length
                        }
                    }
                }
            ]
        };

        _indexBytes = Encoding.UTF8.GetBytes(JsonDefaults.Serialize(index));

        Console.WriteLine($"已生成内容清单：{files.Count} 个文件，共 {Hashing.HumanBytes(files.Sum(f => f.Size))}");
    }

    private static async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();

                var request = await ReadRequestAsync(stream).ConfigureAwait(false);
                if (request is null)
                    return;

                var (method, rawTarget, headers) = request.Value;
                var path = Uri.UnescapeDataString(rawTarget.Split('?')[0]);

                if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteStatusAsync(stream, 405, "Method Not Allowed").ConfigureAwait(false);
                    return;
                }

                // 静态站点模式：URL 路径直接映射到站点目录下的文件，支持 Range，行为与 nginx 一致。
                if (_siteRoot is not null)
                {
                    var relative = path.TrimStart('/');
                    if (relative.Length == 0)
                        relative = "index.json";

                    string full;
                    try
                    {
                        full = SafePath.Resolve(_siteRoot, relative);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[devserver] 拒绝可疑路径 {relative}：{ex.Message}");
                        await WriteStatusAsync(stream, 400, "Bad Request").ConfigureAwait(false);
                        return;
                    }

                    if (!File.Exists(full))
                    {
                        await WriteStatusAsync(stream, 404, "Not Found").ConfigureAwait(false);
                        return;
                    }

                    await ServeFileAsync(stream, full, headers).ConfigureAwait(false);
                    return;
                }

                if (path is "/" or "/index.html")
                {
                    await WriteBytesAsync(stream, 200, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(HelpText())).ConfigureAwait(false);
                    return;
                }

                if (path == "/index.json")
                {
                    await WriteBytesAsync(stream, 200, "application/json; charset=utf-8", _indexBytes).ConfigureAwait(false);
                    return;
                }

                if (path == $"/games/{GameId}/manifest.json")
                {
                    await WriteBytesAsync(stream, 200, "application/json; charset=utf-8", _manifestBytes).ConfigureAwait(false);
                    return;
                }

                var prefix = $"/games/{GameId}/files/";
                if (path.StartsWith(prefix, StringComparison.Ordinal))
                {
                    var relative = path[prefix.Length..];

                    string full;
                    try
                    {
                        // 复用启动器同一套路径守护：即使内容源是"自己人"，也不接受穿越路径。
                        full = SafePath.Resolve(_gameRoot, relative);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[devserver] 拒绝可疑路径 {relative}：{ex.Message}");
                        await WriteStatusAsync(stream, 400, "Bad Request").ConfigureAwait(false);
                        return;
                    }

                    if (!File.Exists(full))
                    {
                        await WriteStatusAsync(stream, 404, "Not Found").ConfigureAwait(false);
                        return;
                    }

                    await ServeFileAsync(stream, full, headers).ConfigureAwait(false);
                    return;
                }

                await WriteStatusAsync(stream, 404, "Not Found").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[devserver] 连接处理失败：{ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static async Task ServeFileAsync(NetworkStream stream, string fullPath, Dictionary<string, string> headers)
    {
        var total = new FileInfo(fullPath).Length;
        long start = 0;
        var end = total - 1;
        var partial = false;

        if (headers.TryGetValue("range", out var rangeValue) &&
            rangeValue.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            var spec = rangeValue["bytes=".Length..].Split(',')[0].Trim();
            var dash = spec.IndexOf('-');

            if (dash >= 0)
            {
                var startText = spec[..dash];
                var endText = spec[(dash + 1)..];

                if (long.TryParse(startText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedStart))
                {
                    start = parsedStart;
                    partial = true;
                }

                if (long.TryParse(endText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedEnd))
                    end = Math.Min(parsedEnd, total - 1);
            }
        }

        if (start >= total)
        {
            var response = $"HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */{total}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response)).ConfigureAwait(false);
            return;
        }

        var length = end - start + 1;

        if (partial)
            Console.WriteLine($"[devserver] 206 {Path.GetFileName(fullPath)} 从第 {start} 字节继续（说明客户端在用断点续传）");

        var header = new StringBuilder();
        header.Append(partial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
        header.Append($"Content-Type: {ContentTypeFor(fullPath)}\r\n");
        header.Append("Accept-Ranges: bytes\r\n");
        header.Append($"Content-Length: {length}\r\n");
        if (partial)
            header.Append($"Content-Range: bytes {start}-{end}/{total}\r\n");
        header.Append("Connection: close\r\n\r\n");

        await stream.WriteAsync(Encoding.ASCII.GetBytes(header.ToString())).ConfigureAwait(false);

        var willDrop = _dropsRemaining > 0 && _dropAfter > 0;
        if (willDrop)
        {
            _dropsRemaining--;
            Console.WriteLine($"[devserver] 故意在对 {Path.GetFileName(fullPath)} 传输 {Math.Min(_dropAfter, length)} 字节后掐断连接");
        }

        var budget = willDrop ? Math.Min(_dropAfter, length) : length;

        await using var file = new FileStream(
            fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);

        file.Seek(start, SeekOrigin.Begin);

        var buffer = new byte[1 << 16];
        long remainingBytes = budget;
        long sent = 0;
        var stopwatch = Stopwatch.StartNew();

        while (remainingBytes > 0)
        {
            var want = (int)Math.Min(buffer.Length, remainingBytes);
            var read = await file.ReadAsync(buffer.AsMemory(0, want)).ConfigureAwait(false);
            if (read <= 0)
                break;

            await stream.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            remainingBytes -= read;
            sent += read;

            if (_throttleBytesPerSecond > 0)
            {
                var expectedSeconds = sent / (double)_throttleBytesPerSecond;
                var actualSeconds = stopwatch.Elapsed.TotalSeconds;
                if (expectedSeconds > actualSeconds)
                    await Task.Delay(TimeSpan.FromSeconds(expectedSeconds - actualSeconds)).ConfigureAwait(false);
            }
        }

        await stream.FlushAsync().ConfigureAwait(false);
    }

    private static async Task<(string Method, string Target, Dictionary<string, string> Headers)?> ReadRequestAsync(NetworkStream stream)
    {
        var sb = new StringBuilder();
        var one = new byte[1];

        while (sb.Length < 16384)
        {
            var read = await stream.ReadAsync(one.AsMemory(0, 1)).ConfigureAwait(false);
            if (read == 0)
                return null;

            sb.Append((char)one[0]);

            if (sb.Length >= 4 &&
                sb[sb.Length - 4] == '\r' && sb[sb.Length - 3] == '\n' &&
                sb[sb.Length - 2] == '\r' && sb[sb.Length - 1] == '\n')
                break;
        }

        var lines = sb.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
            return null;

        var parts = lines[0].Split(' ');
        if (parts.Length < 2)
            return null;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var colon = lines[i].IndexOf(':');
            if (colon > 0)
                headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
        }

        return (parts[0], parts[1], headers);
    }

    private static async Task WriteBytesAsync(NetworkStream stream, int status, string contentType, byte[] body)
    {
        var header =
            $"HTTP/1.1 {status} {Reason(status)}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(header)).ConfigureAwait(false);
        await stream.WriteAsync(body).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    private static async Task WriteStatusAsync(NetworkStream stream, int status, string message)
    {
        await WriteBytesAsync(stream, status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(message)).ConfigureAwait(false);
    }

    private static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".json" => "application/json; charset=utf-8",
        ".txt" or ".md" => "text/plain; charset=utf-8",
        ".html" or ".htm" => "text/html; charset=utf-8",
        _ => "application/octet-stream"
    };

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        206 => "Partial Content",
        400 => "Bad Request",
        404 => "Not Found",
        405 => "Method Not Allowed",
        416 => "Range Not Satisfiable",
        _ => "OK"
    };

    private static string HelpText() =>
        $"""
        0verClient 本地内容源
          GET /index.json
          GET /games/{GameId}/manifest.json
          GET /games/{GameId}/files/<相对路径>   （支持 Range，用于断点续传）
        """;

    private static string? FindDemoGameOutput()
    {
        var roots = new List<string>();

        void WalkUp(string start)
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                roots.Add(dir.FullName);
                if (File.Exists(Path.Combine(dir.FullName, "0verClient.slnx")))
                    break;
                dir = dir.Parent;
            }
        }

        WalkUp(AppContext.BaseDirectory);
        WalkUp(Directory.GetCurrentDirectory());

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var bin = Path.Combine(root, "samples", "DemoGame", "bin");
            if (!Directory.Exists(bin))
                continue;

            var exe = Directory.EnumerateFiles(bin, "DemoGame.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();

            if (exe is not null)
                return Path.GetDirectoryName(exe);
        }

        return null;
    }

    private static string? GetString(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        var prefix = name + "=";
        var inline = args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return inline?[prefix.Length..];
    }

    private static int GetInt(string[] args, string name, int fallback) =>
        int.TryParse(GetString(args, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static double GetDouble(string[] args, string name, double fallback) =>
        double.TryParse(GetString(args, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;
}
