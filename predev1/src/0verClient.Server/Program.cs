using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using OverClient.Core.Util;

namespace OverClient.Server;

/// <summary>
/// 0verClient content server.
///
/// 这是 tools/serve-site.ps1 的正式版：把发布出来的静态站点目录用 HTTP 提供出去，
/// 支持 Range（断点续传）。做成 exe 是为了迁移方便 —— 换一台服务器时，
/// 把 exe + site 目录拷过去运行即可，不需要装任何东西（用便携或自包含打包时）。
///
/// 与 PowerShell 版的区别（都是修过的坑）：
///   - 每个连接独立处理：一个只连不发数据的空连接不会拖住别人
///   - 空闲连接有超时，会被主动断开
///   - 启动时就把 index.json 读一遍，直接告诉你里面有几个游戏
/// </summary>
internal static class Program
{
    private static string _root = string.Empty;
    private static long _throttleBytesPerSecond;
    private static int _idleTimeoutMs = 5000;
    private static long _requests;
    private static long _bytesSent;
    private static readonly CancellationTokenSource Shutdown = new();

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length == 0 || Has(args, "--help") || Has(args, "-h"))
        {
            Usage();
            return args.Length == 0 ? 1 : 0;
        }

        var root = Arg(args, "--root");
        if (string.IsNullOrWhiteSpace(root))
        {
            Console.Error.WriteLine("ERROR: --root is required.");
            Usage();
            return 1;
        }

        _root = Path.GetFullPath(root);
        if (!Directory.Exists(_root))
        {
            Console.Error.WriteLine($"ERROR: root folder not found: {_root}");
            return 1;
        }

        var port = int.TryParse(Arg(args, "--port"), out var p) ? p : 8787;
        var bind = Arg(args, "--bind") ?? "0.0.0.0";
        var throttleKbps = double.TryParse(Arg(args, "--throttle-kbps"), out var t) ? t : 0;
        _throttleBytesPerSecond = (long)(throttleKbps * 1024);
        _idleTimeoutMs = int.TryParse(Arg(args, "--idle-timeout-ms"), out var it) ? it : 5000;

        if (Has(args, "--add-firewall-rule"))
            TryAddFirewallRule(port);

        // 启动就把 index.json 看一遍 —— 这一个检查能省掉后面"游戏库是空的"的来回猜测。
        DescribeContent();

        IPAddress bindAddress;
        if (bind is "0.0.0.0" or "*")
        {
            bindAddress = IPAddress.Any;
        }
        else if (!IPAddress.TryParse(bind, out bindAddress!))
        {
            Console.Error.WriteLine($"ERROR: --bind '{bind}' is not a valid IP address. Use 0.0.0.0 or 127.0.0.1.");
            return 1;
        }

        var listener = new TcpListener(bindAddress, port);
        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: could not listen on port {port}");
            Console.Error.WriteLine($"       detail: {ex.Message}");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Something is already using that port. Most likely an older copy of this");
            Console.Error.WriteLine("server is still running. Find and stop it:");
            Console.Error.WriteLine($"    netstat -ano | findstr :{port}");
            Console.Error.WriteLine("    Stop-Process -Id <PID> -Force");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Do NOT just switch ports: the port is baked into the launcher settings and");
            Console.Error.WriteLine("into the absolute file URLs of the published manifest. A new port means");
            Console.Error.WriteLine("re-running Publish.exe with the new address and re-copying the site.");
            return 1;
        }

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Shutdown.Cancel();
        };

        WriteBanner(port, bind);

        using var slots = new SemaphoreSlim(64, 64);

        try
        {
            while (!Shutdown.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(Shutdown.Token).ConfigureAwait(false);

                // 每个连接独立处理 —— 这是与 PowerShell 版最大的区别：
                // 一个空连接再也不可能拖住整个服务。
                await slots.WaitAsync(Shutdown.Token).ConfigureAwait(false);
                _ = Task.Run(() =>
                {
                    try
                    {
                        HandleClient(client);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"  ERR {ex.GetType().Name}: {ex.Message}");
                    }
                    finally
                    {
                        slots.Release();
                        try { client.Dispose(); } catch { /* ignore */ }
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C
        }
        finally
        {
            listener.Stop();
        }

        Console.WriteLine();
        Console.WriteLine($"stopped. served {_requests} request(s), {Hashing.HumanBytes(_bytesSent)} total.");
        return 0;
    }

    // ---------------------------------------------------------------- content

    private sealed record GameInfo(string Id, string Name, string Version, string ManifestUrl);

    private static void DescribeContent()
    {
        var indexPath = Path.Combine(_root, "index.json");

        if (!File.Exists(indexPath))
        {
            Warn("index.json is NOT in the site root");
            Warn("copy the CONTENTS of the published site folder here - index.json must sit at the root");
            return;
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(indexPath);
        }
        catch (Exception ex)
        {
            Warn($"index.json could not be read: {ex.Message}");
            return;
        }

        if (!TrySummarizeIndex(bytes, out var games, out var error))
        {
            Warn($"index.json could not be parsed: {error}");
            return;
        }

        Console.WriteLine($"content: {games.Count} game(s) in index.json");

        foreach (var game in games)
        {
            Console.WriteLine($"  - {game.Id}  \"{game.Name}\"  v{game.Version}");

            if (game.ManifestUrl.Contains("127.0.0.1", StringComparison.Ordinal) ||
                game.ManifestUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase))
            {
                Warn($"    {game.Id}: manifest url points at {game.ManifestUrl}");
                Warn("    this package was built with a LOCAL address; players would download from their own machine");
                Warn("    fix: re-run Publish with the server's public address, then copy the site again");
            }
        }
    }

    /// <summary>
    /// 用 Utf8JsonReader 直接读 index.json 里启动时要报告的那点信息。
    ///
    /// 为什么不复用 Core 的 JsonSerializer：那是反射序列化，会阻止 NativeAOT 与裁剪
    /// （要支持就得改成源生成）。而服务器只需要"游戏数量 + id/name/version/清单地址"，
    /// 手写一遍更小、更快，而且完全不依赖反射 —— 这正是能把 77 MB 压到几 MB 的前提。
    /// </summary>
    private static bool TrySummarizeIndex(byte[] utf8, out List<GameInfo> games, out string error)
    {
        games = [];
        error = string.Empty;

        try
        {
            var reader = new Utf8JsonReader(utf8, new JsonReaderOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });

            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName || !reader.ValueTextEquals("games"))
                    continue;

                if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
                {
                    error = "\"games\" is not an array";
                    return false;
                }

                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType == JsonTokenType.StartObject)
                        games.Add(ReadGame(ref reader));
                    else
                        reader.Skip();
                }

                return true;
            }

            error = "no \"games\" property found";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static GameInfo ReadGame(ref Utf8JsonReader reader)
    {
        var id = string.Empty;
        var name = string.Empty;
        var version = string.Empty;
        var url = string.Empty;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;

            if (reader.ValueTextEquals("id"))
            {
                reader.Read();
                id = reader.GetString() ?? string.Empty;
            }
            else if (reader.ValueTextEquals("name"))
            {
                reader.Read();
                name = reader.GetString() ?? string.Empty;
            }
            else if (reader.ValueTextEquals("channels"))
            {
                ReadFirstChannel(ref reader, out version, out url);
            }
            else
            {
                reader.Skip();
            }
        }

        return new GameInfo(id, name, version, url);
    }

    private static void ReadFirstChannel(ref Utf8JsonReader reader, out string version, out string url)
    {
        version = string.Empty;
        url = string.Empty;

        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            return;

        var first = true;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;

            if (!first)
            {
                reader.Skip();
                continue;
            }

            first = false;

            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return;

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                    continue;

                if (reader.ValueTextEquals("version"))
                {
                    reader.Read();
                    version = reader.GetString() ?? string.Empty;
                }
                else if (reader.ValueTextEquals("manifestUrl"))
                {
                    reader.Read();
                    url = reader.GetString() ?? string.Empty;
                }
                else
                {
                    reader.Skip();
                }
            }
        }
    }

    // ---------------------------------------------------------------- http

    private static void HandleClient(TcpClient client)
    {
        client.NoDelay = true;
        client.ReceiveTimeout = _idleTimeoutMs;
        client.SendTimeout = 30000;

        var stream = client.GetStream();
        var raw = ReadHeader(stream);
        if (string.IsNullOrEmpty(raw))
            return;

        var lines = raw.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
            return;

        var parts = lines[0].Split(' ');
        if (parts.Length < 2)
            return;

        var method = parts[0].ToUpperInvariant();
        var target = parts[1];

        if (method is not ("GET" or "HEAD"))
        {
            SendText(stream, 405, "Method Not Allowed", "Only GET/HEAD are supported.");
            return;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var colon = lines[i].IndexOf(':');
            if (colon > 0)
                headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
        }

        var path = Uri.UnescapeDataString(target.Split('?')[0]);

        // A bare "/" is a person opening the address in a browser: show the readme if it
        // is there, otherwise the index.
        if (path is "/" or "")
            path = File.Exists(Path.Combine(_root, "index.html")) ? "/index.html" : "/index.json";

        string full;
        try
        {
            full = SafePath.Resolve(_root, path.TrimStart('/'));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  400 rejected path: {path}  ({ex.Message})");
            SendText(stream, 400, "Bad Request", "Path not allowed.");
            return;
        }

        if (!File.Exists(full))
        {
            Console.WriteLine($"  404 {path}");
            SendText(stream, 404, "Not Found", "Not found.");
            return;
        }

        SendFile(stream, full, headers, method == "HEAD");
    }

    /// <summary>读请求头。空闲超时由 socket 的 ReceiveTimeout 负责。</summary>
    private static string? ReadHeader(NetworkStream stream)
    {
        var sb = new StringBuilder(512);
        var one = new byte[1];

        while (sb.Length < 16384)
        {
            int read;
            try
            {
                read = stream.Read(one, 0, 1);
            }
            catch (IOException)
            {
                // 客户端连上了却一个字节都没发（浏览器预连接就是这样），超时后被丢弃。
                // 有并发处理，所以它只是占着自己那条线程，不会拖住别人。
                Interlocked.Increment(ref _idleDrops);
                Console.WriteLine("  idle connection dropped (connected but sent no request)");
                return null;
            }

            if (read <= 0)
                return null;

            sb.Append((char)one[0]);

            if (sb.Length >= 4 &&
                sb[^4] == '\r' && sb[^3] == '\n' && sb[^2] == '\r' && sb[^1] == '\n')
            {
                break;
            }
        }

        return sb.ToString();
    }

    private static int _idleDrops;

    private static void SendFile(NetworkStream stream, string fullPath, Dictionary<string, string> headers, bool headOnly)
    {
        var info = new FileInfo(fullPath);
        var total = info.Length;

        long start = 0;
        var end = total - 1;
        var partial = false;

        if (headers.TryGetValue("range", out var rangeValue) && rangeValue.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            var spec = rangeValue[6..].Split(',')[0].Trim();
            var dash = spec.IndexOf('-');

            if (dash >= 0)
            {
                if (long.TryParse(spec[..dash], out var parsedStart))
                {
                    start = parsedStart;
                    partial = true;
                }

                if (long.TryParse(spec[(dash + 1)..], out var parsedEnd))
                    end = Math.Min(parsedEnd, total - 1);
            }
        }

        if (total > 0 && start >= total)
        {
            var response = $"HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */{total}\r\nConnection: close\r\n\r\n";
            Write(stream, Encoding.ASCII.GetBytes(response));
            return;
        }

        var length = total == 0 ? 0 : end - start + 1;

        var header = new StringBuilder()
            .Append(partial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n")
            .Append($"Content-Type: {ContentTypeFor(fullPath)}\r\n")
            .Append("Accept-Ranges: bytes\r\n")
            .Append($"Content-Length: {length}\r\n");

        if (partial)
            header.Append($"Content-Range: bytes {start}-{end}/{total}\r\n");

        header.Append("Connection: close\r\n\r\n");
        Write(stream, Encoding.ASCII.GetBytes(header.ToString()));

        var name = Path.GetRelativePath(_root, fullPath);

        if (partial)
            Console.WriteLine($"  206 {name} (from byte {start})");
        else
            Console.WriteLine($"  200 {name} ({total} bytes)");

        if (headOnly || length == 0)
        {
            stream.Flush();
            return;
        }

        using var file = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        file.Seek(start, SeekOrigin.Begin);

        var buffer = new byte[1 << 16];
        long remaining = length;
        long sent = 0;
        var stopwatch = Stopwatch.StartNew();

        while (remaining > 0)
        {
            var want = (int)Math.Min(buffer.Length, remaining);
            var read = file.Read(buffer, 0, want);
            if (read <= 0)
                break;

            stream.Write(buffer, 0, read);
            remaining -= read;
            sent += read;

            if (_throttleBytesPerSecond > 0)
            {
                var expected = sent / (double)_throttleBytesPerSecond;
                var actual = stopwatch.Elapsed.TotalSeconds;
                if (expected > actual)
                    Thread.Sleep(TimeSpan.FromSeconds(expected - actual));
            }
        }

        stream.Flush();

        Interlocked.Increment(ref _requests);
        Interlocked.Add(ref _bytesSent, sent);
    }

    private static void SendText(NetworkStream stream, int status, string reason, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var header = $"HTTP/1.1 {status} {reason}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
        Write(stream, Encoding.ASCII.GetBytes(header));
        Write(stream, bytes);
        stream.Flush();
    }

    private static void Write(NetworkStream stream, byte[] bytes) => stream.Write(bytes, 0, bytes.Length);

    private static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".json" => "application/json; charset=utf-8",
        ".txt" or ".md" => "text/plain; charset=utf-8",
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" => "application/javascript; charset=utf-8",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".svg" => "image/svg+xml",
        ".ico" => "image/x-icon",
        ".zip" => "application/zip",
        ".7z" => "application/x-7z-compressed",
        _ => "application/octet-stream"
    };

    // ---------------------------------------------------------------- firewall

    private static void TryAddFirewallRule(int port)
    {
        var name = $"0verClient content {port}";

        try
        {
            var psi = new ProcessStartInfo("netsh")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("advfirewall");
            psi.ArgumentList.Add("firewall");
            psi.ArgumentList.Add("add");
            psi.ArgumentList.Add("rule");
            psi.ArgumentList.Add($"name={name}");
            psi.ArgumentList.Add("dir=in");
            psi.ArgumentList.Add("action=allow");
            psi.ArgumentList.Add("protocol=TCP");
            psi.ArgumentList.Add($"localport={port}");

            using var process = Process.Start(psi);
            if (process is null)
            {
                Warn("could not start netsh; add the firewall rule by hand");
                return;
            }

            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(15000);

            if (process.ExitCode == 0)
            {
                Console.WriteLine($"Windows Firewall: inbound TCP {port} allowed.");
            }
            else
            {
                Warn($"Windows Firewall: could not add the rule (netsh exit {process.ExitCode})");
                Warn("run this program as Administrator, or add the rule by hand:");
                Warn($"    netsh advfirewall firewall add rule name=\"{name}\" dir=in action=allow protocol=TCP localport={port}");
                if (!string.IsNullOrWhiteSpace(output))
                    Warn($"    detail: {output.Trim()}");
            }
        }
        catch (Exception ex)
        {
            Warn($"Windows Firewall: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- output

    private static void WriteBanner(int port, string bind)
    {
        Console.WriteLine();
        Console.WriteLine("============================================================");
        Console.WriteLine(" 0verClient content server");
        Console.WriteLine("============================================================");
        Console.WriteLine($"  root        : {_root}");
        Console.WriteLine($"  listening   : {bind}:{port}{(bind == "0.0.0.0" ? " (all network interfaces)" : " (this machine only)")}");
        Console.WriteLine($"  throttle    : {(_throttleBytesPerSecond > 0 ? $"{_throttleBytesPerSecond / 1024} KB/s" : "off")}");
        Console.WriteLine($"  idle timeout: {_idleTimeoutMs} ms (a connection that sends no request is dropped)");
        Console.WriteLine($"  concurrency : one thread per connection (idle connections dropped: {_idleDrops})");
        Console.WriteLine();
        Console.WriteLine($"  Local test  : http://127.0.0.1:{port}/index.json");
        if (bind == "0.0.0.0")
            Console.WriteLine($"  From outside: http://<this machine's public IP>:{port}/index.json");
        Console.WriteLine();
        Console.WriteLine("Press Ctrl+C to stop.");
        Console.WriteLine();
    }

    private static void Warn(string message)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"WARNING: {message}");
        Console.ForegroundColor = previous;
    }

    private static void Usage()
    {
        Console.WriteLine("""
        0verClient content server - serves a published site folder over HTTP with Range support.

        Usage:
          server --root <siteDir> [options]

        Required:
          --root <dir>            folder produced by Publish.exe (index.json must be at its root)

        Options:
          --port <n>              listen port (default 8787)
          --bind <ip>             bind address (default 0.0.0.0 = all interfaces; 127.0.0.1 = local only)
          --throttle-kbps <n>     rate limit per connection (default 0 = unlimited)
          --idle-timeout-ms <n>   drop connections that send no request (default 5000)
          --add-firewall-rule     also open the Windows Firewall port (needs Administrator)
          --help                  show this help

        Example:
          server.exe --root C:\0verclient\site --port 8787 --add-firewall-rule
        """);
    }

    private static bool Has(string[] args, string name) =>
        args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    private static string? Arg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        var prefix = name + "=";
        return args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..];
    }
}
