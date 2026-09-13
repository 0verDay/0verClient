using System.Text;
using OverClient.Core;
using OverClient.Core.Manifest;
using OverClient.Core.Util;

namespace OverClient.Publish;

/// <summary>
/// 把任意游戏目录打包成一个静态站点，可以直接扔到腾讯云 / nginx / 对象存储上：
///
///   &lt;out&gt;/index.json                        游戏列表 + 各通道清单的地址与 sha256
///   &lt;out&gt;/games/&lt;id&gt;/manifest.json           文件清单（路径 / 大小 / sha256）
///   &lt;out&gt;/games/&lt;id&gt;/files/...               游戏文件本体
///
/// 重复运行会**合并**进已有的 index.json，所以可以一个一个游戏往上加，不用一次全传。
///
/// 注意：控制台输出刻意只用 ASCII。
/// Windows PowerShell 5.1 会按 ANSI 代码页解码子进程输出，中文会变乱码，
/// 而这个工具的输出主要是给你复制粘贴命令用的，不能花。
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || Has(args, "--help") || Has(args, "-h"))
        {
            Usage();
            return args.Length == 0 ? 1 : 0;
        }

        var gameDir = Arg(args, "--game");
        var id = Arg(args, "--id");
        var siteRoot = Arg(args, "--out");
        var baseUrl = Arg(args, "--base-url")?.TrimEnd('/');

        if (gameDir is null || id is null || siteRoot is null || baseUrl is null)
        {
            Console.Error.WriteLine("ERROR: --game, --id, --out and --base-url are all required.");
            Console.Error.WriteLine();
            Usage();
            return 1;
        }

        if (!Directory.Exists(gameDir))
        {
            Console.Error.WriteLine($"ERROR: game directory not found: {gameDir}");
            return 1;
        }

        string safeId;
        try
        {
            // 复用启动器同一套校验：id 会变成目录名，绝不会让它带分隔符或保留设备名。
            safeId = SafePath.SanitizeSegment(id);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: invalid --id: {ex.Message}");
            return 1;
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp))
        {
            Console.Error.WriteLine($"ERROR: --base-url must be an absolute http(s) URL, e.g. https://cdn.example.com/0verclient");
            return 1;
        }

        if (baseUri.Scheme == Uri.UriSchemeHttp && !baseUri.IsLoopback)
        {
            Console.WriteLine("NOTE: base-url is plain http. The launcher refuses insecure http by default.");
            Console.WriteLine("      You must tick 'allow plain http' AND list the host in the whitelist.");
            Console.WriteLine("      See docs/SERVER.md, section 0.");
            Console.WriteLine();
        }

        var name = Arg(args, "--name") ?? safeId;
        var version = Arg(args, "--version") ?? "1.0.0";
        var channel = Arg(args, "--channel") ?? "latest";
        var summary = Arg(args, "--summary");
        var accent = Arg(args, "--accent") ?? "#4C8DFF";
        var tags = (Arg(args, "--tags") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var keepPdb = Has(args, "--keep-pdb");
        var launchArgs = AllArgs(args, "--launch-arg");

        // ---- 1. 收集文件、计算哈希 ----
        var entries = new List<FileEntry>();
        var copyPlan = new List<(string Source, string Relative)>();
        var skipped = 0;

        foreach (var source in Directory.EnumerateFiles(gameDir, "*", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(gameDir, source).Replace('\\', '/');

            if (!keepPdb && relative.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
            {
                skipped++;
                continue;
            }

            if (relative.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ||
                relative.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            {
                skipped++;
                continue;
            }

            string normalized;
            try
            {
                normalized = SafePath.NormalizeRelative(relative);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"ERROR: refusing file with an unsafe path '{relative}': {ex.Message}");
                return 1;
            }

            var info = new FileInfo(source);
            entries.Add(new FileEntry
            {
                Path = normalized,
                Size = info.Length,
                Sha256 = await Hashing.Sha256FileAsync(source),
                Executable = IsExecutable(normalized)
            });
            copyPlan.Add((source, normalized));
        }

        if (entries.Count == 0)
        {
            Console.Error.WriteLine($"ERROR: no publishable files found under {gameDir}");
            return 1;
        }

        // ---- 2. 决定启动入口 ----
        var launch = Arg(args, "--launch");

        if (launch is not null)
        {
            var normalized = SafePath.NormalizeRelative(launch);
            if (!entries.Any(e => string.Equals(e.Path, normalized, StringComparison.OrdinalIgnoreCase)))
            {
                Console.Error.WriteLine($"ERROR: --launch '{launch}' is not in the file list.");
                return 1;
            }

            launch = normalized;
        }
        else
        {
            launch = entries
                .Where(e => e.Executable)
                .OrderBy(e => e.Path.Count(c => c == '/'))
                .ThenBy(e => e.Path, StringComparer.Ordinal)
                .FirstOrDefault()?.Path;

            if (launch is null)
            {
                Console.Error.WriteLine("ERROR: no .exe/.cmd/.bat/.com found; pass --launch <relative path>.");
                return 1;
            }
        }

        var totalBytes = entries.Sum(e => e.Size);

        // ---- 3. 拷贝文件本体 ----
        var filesRoot = Path.Combine(siteRoot, "games", safeId, "files");
        foreach (var (source, relative) in copyPlan)
        {
            var destination = SafePath.Resolve(filesRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: true);
        }

        // ---- 4. 生成清单，并立刻对写出的字节算哈希（哈希必须和磁盘上的字节一致）----
        var manifest = new GameManifest
        {
            SchemaVersion = GameManifest.CurrentSchema,
            GameId = safeId,
            Name = name,
            Version = version,
            Channel = channel,
            PublishedAt = DateTimeOffset.UtcNow,
            ReleaseNotes = Arg(args, "--notes"),
            LauncherMinVersion = Arg(args, "--min-launcher") ?? "0.1.0",
            BaseUrl = $"{baseUrl}/games/{safeId}/files/",
            Files = entries,
            Launch = new LaunchSpec
            {
                Executable = launch,
                Arguments = launchArgs,
                WorkingDirectory = "."
            }
        };

        var manifestBytes = Encoding.UTF8.GetBytes(JsonDefaults.Serialize(manifest));
        var manifestPath = Path.Combine(siteRoot, "games", safeId, "manifest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        await File.WriteAllBytesAsync(manifestPath, manifestBytes);
        var manifestSha = Hashing.Sha256Bytes(manifestBytes);

        // ---- 5. 并入 index.json（保留其它游戏）----
        var indexPath = Path.Combine(siteRoot, "index.json");
        RootIndex index;

        if (File.Exists(indexPath))
        {
            try
            {
                index = JsonDefaults.Deserialize<RootIndex>(await File.ReadAllBytesAsync(indexPath));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"ERROR: existing index.json is unreadable: {ex.Message}");
                return 1;
            }
        }
        else
        {
            index = new RootIndex();
        }

        var replaced = index.Games.RemoveAll(g => string.Equals(g.Id, safeId, StringComparison.OrdinalIgnoreCase)) > 0;

        index.Games.Add(new GameEntry
        {
            Id = safeId,
            Name = name,
            Summary = summary,
            AccentColor = accent,
            Tags = tags,
            Channels =
            {
                [channel] = new ChannelEntry
                {
                    Version = version,
                    ManifestUrl = $"{baseUrl}/games/{safeId}/manifest.json",
                    ManifestSha256 = manifestSha,
                    ManifestSize = manifestBytes.Length
                }
            }
        });

        index.GeneratedAt = DateTimeOffset.UtcNow;
        index.Games.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));

        await File.WriteAllBytesAsync(indexPath, Encoding.UTF8.GetBytes(JsonDefaults.Serialize(index)));

        // ---- 6. 报告 ----
        var siteFull = Path.GetFullPath(siteRoot);
        Console.WriteLine(replaced ? "Game updated in index.json" : "Game added to index.json");
        Console.WriteLine();
        Console.WriteLine($"  game id      : {safeId}");
        Console.WriteLine($"  version      : {version}  (channel: {channel})");
        Console.WriteLine($"  launch entry : {launch}");
        Console.WriteLine($"  files        : {entries.Count} ({Hashing.HumanBytes(totalBytes)})"
                          + (skipped > 0 ? $", skipped {skipped} (.pdb/.part/.tmp)" : ""));
        Console.WriteLine($"  manifest sha : {manifestSha}");
        Console.WriteLine($"  site root    : {siteFull}");
        Console.WriteLine($"  games in idx : {index.Games.Count} ({string.Join(", ", index.Games.Select(g => g.Id))})");
        Console.WriteLine();
        Console.WriteLine("Next steps:");
        Console.WriteLine($"  1) copy the site root to your server : {siteFull}");
        Console.WriteLine($"  2) point the launcher at             : {baseUrl}/index.json");
        Console.WriteLine();
        Console.WriteLine("Full walkthrough: docs/SERVER.md");

        return 0;
    }

    private static bool IsExecutable(string relativePath)
    {
        var extension = Path.GetExtension(relativePath);
        return extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".com", StringComparison.OrdinalIgnoreCase);
    }

    private static void Usage()
    {
        Console.WriteLine("""
        0verClient publisher -- turn a game folder into a static content site.

        Usage:
          Publish --game <folder> --id <gameId> --out <siteDir> --base-url <publicUrl> [options]

        Required:
          --game <folder>        folder containing the game files (recursively)
          --id <gameId>          stable id, also used as the folder name, e.g. testpack
          --out <siteDir>        output site root (created if missing; safe to re-run)
          --base-url <url>       public URL prefix of <siteDir>, e.g. https://cdn.example.com/0verclient

        Optional:
          --name <text>          display name (default: same as --id)
          --version <text>       version string (default: 1.0.0)
          --channel <name>       channel name (default: latest)
          --summary <text>       short description shown on the game card
          --accent <#RRGGBB>     card accent color (default: #4C8DFF)
          --tags <a,b,c>         comma separated tags
          --notes <text>         release notes
          --launch <relpath>     launch entry; auto-detected (.exe/.cmd/.bat) when omitted
          --launch-arg <value>   launch argument; repeatable
          --min-launcher <ver>   minimum launcher version (default: 0.1.0)
          --keep-pdb             include .pdb files (excluded by default)
          --help                 show this help

        Example:
          Publish --game samples\TestPack --id testpack --name "Server Test Pack" ^
                  --version 1.0.0 --base-url http://1.2.3.4:8787 --out build\site ^
                  --summary "Downloaded from my Tencent Cloud server"

        Re-running with the same --id updates that game and keeps the others intact.
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

    private static List<string> AllArgs(string[] args, string name)
    {
        var values = new List<string>();

        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                values.Add(args[i + 1]);
        }

        return values;
    }
}
