using System.Diagnostics;
using System.Text;
using OverClient.Core;
using OverClient.Core.Install;
using OverClient.Core.Launch;
using OverClient.Core.Manifest;
using OverClient.Core.Net;
using OverClient.Core.Util;

namespace OverClient.SmokeTest;

/// <summary>
/// 引擎自检：不开界面，直接把"取索引 → 校验清单 → 下载 → 落盘 → 增量复用"整条链路跑一遍。
///
/// 存在的意义：启动器最容易出错的从来不是 UI，而是下载/校验/事务这三件事。
/// 有了它，改 Core 之后可以立刻知道有没有把安装链路改坏，而且能在 CI 里跑。
///
/// 用法:
///   SmokeTest --index http://127.0.0.1:8787/index.json [--games &lt;目录&gt;] [--fresh]
/// </summary>
internal static class Program
{
    private static int _failures;
    private static int _checks;

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Log.Init();

        var indexUrl = GetArg(args, "--index") ?? "http://127.0.0.1:8787/index.json";
        var gamesRoot = GetArg(args, "--games")
            ?? Path.Combine(Path.GetTempPath(), "0verclient-smoketest", "games");
        var fresh = args.Contains("--fresh", StringComparer.OrdinalIgnoreCase);
        var verbose = args.Contains("--verbose", StringComparer.OrdinalIgnoreCase);
        var launchCheck = args.Contains("--launch-check", StringComparer.OrdinalIgnoreCase);

        if (verbose)
            Log.LineWritten += line => Console.WriteLine("      " + line);

        // 对着真实服务器（比如腾讯云那台）跑自检时，可能需要放行明文 http。
        // 规则和启动器完全一致：必须显式打开，且主机必须显式列出。
        var allowHttp = args.Contains("--allow-http", StringComparer.OrdinalIgnoreCase);
        var hosts = (GetArg(args, "--hosts") ?? "")
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (allowHttp && Uri.TryCreate(indexUrl, UriKind.Absolute, out var indexUri) && !indexUri.IsLoopback &&
            !hosts.Contains(indexUri.Host, StringComparer.OrdinalIgnoreCase))
        {
            hosts.Add(indexUri.Host);
            Console.WriteLine($"注意：已放行明文 http，并自动把 {indexUri.Host} 加入白名单（仅本自检工具如此）");
        }

        if (fresh && Directory.Exists(gamesRoot))
        {
            Console.WriteLine($"清空旧安装目录 {gamesRoot}");
            Directory.Delete(gamesRoot, recursive: true);
        }

        Console.WriteLine("0verClient 引擎自检");
        Console.WriteLine($"  索引     {indexUrl}");
        Console.WriteLine($"  安装目录 {gamesRoot}");
        Console.WriteLine();

        Console.WriteLine("[1] 路径守护");
        CheckPathGuards();

        Console.WriteLine();
        Console.WriteLine("[2] 内容源");

        using var source = new HttpContentSource();
        var policy = new UrlPolicy(hosts, allowHttp);
        var manifests = new ManifestService(source, policy);
        var installer = new Installer(source);

        RootIndex index;
        try
        {
            index = await manifests.LoadIndexAsync(indexUrl);
        }
        catch (Exception ex)
        {
            return Fail($"读取索引失败：{ex.GetType().Name}: {ex.Message}");
        }

        Check($"索引可加载（{index.Games.Count} 个游戏）", index.Games.Count > 0);

        var entry = index.Games[0];

        GameManifest manifest;
        try
        {
            manifest = await manifests.LoadManifestAsync(entry, "latest");
        }
        catch (Exception ex)
        {
            return Fail($"读取清单失败：{ex.GetType().Name}: {ex.Message}");
        }

        Check($"清单可加载（{manifest.Name} {manifest.Version}，{manifest.Files.Count} 个文件，{Hashing.HumanBytes(manifest.TotalBytes)}）", manifest.Files.Count > 0);
        Check("清单哈希已被校验", !string.IsNullOrWhiteSpace(entry.DefaultChannel()?.ManifestSha256));

        Console.WriteLine();
        Console.WriteLine("[3] 首次安装");

        InstallResult first;
        try
        {
            var stopwatch = Stopwatch.StartNew();
            first = await installer.InstallAsync(manifest, gamesRoot, null);
            Console.WriteLine($"     耗时 {stopwatch.Elapsed.TotalSeconds:0.00}s · 下载 {first.DownloadedFiles} · 复用 {first.ReusedFiles}");
        }
        catch (Exception ex)
        {
            return Fail($"首次安装失败：{ex.GetType().Name}: {ex.Message}");
        }

        CheckFilesMatchManifest(manifest, first.InstallDir);
        Check("启动文件存在", File.Exists(SafePath.Resolve(first.InstallDir, manifest.Launch.Executable)));
        Check("没有残留的 .part 临时文件", !Directory.EnumerateFiles(first.InstallDir, "*.part", SearchOption.AllDirectories).Any());

        Console.WriteLine();
        Console.WriteLine("[4] 二次安装（增量）");

        InstallResult second;
        try
        {
            second = await installer.InstallAsync(manifest, gamesRoot, null);
            Console.WriteLine($"     下载 {second.DownloadedFiles} · 复用 {second.ReusedFiles}");
        }
        catch (Exception ex)
        {
            return Fail($"增量安装失败：{ex.GetType().Name}: {ex.Message}");
        }

        Check("增量安装全部复用（硬链接生效，未重新下载）",
            second.ReusedFiles == manifest.Files.Count && second.DownloadedFiles == 0);
        CheckFilesMatchManifest(manifest, second.InstallDir);
        Check("staging 目录已清理", !Directory.EnumerateDirectories(gamesRoot, ".staging-*").Any());
        Check("旧版本目录已清理", !Directory.EnumerateDirectories(gamesRoot, "*.old-*").Any());

        Console.WriteLine();
        Console.WriteLine("[5] 进度对象格式化（这些代码跑在 UI 线程上，抛异常就会打断安装）");
        CheckProgressFormatting();

        if (launchCheck)
        {
            Console.WriteLine();
            Console.WriteLine("[6] 启动进程（覆盖 .cmd / .bat 分支 —— 它们无法被 CreateProcess 直接执行）");
            CheckLaunch(manifest, second.InstallDir);
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine("[6] 已跳过启动检查（加 --launch-check 会真的把游戏跑起来再结束它）");
        }

        Console.WriteLine();
        Console.WriteLine(_failures == 0
            ? $"全部通过（{_checks} 项检查）"
            : $"{_failures} / {_checks} 项检查失败");

        return _failures == 0 ? 0 : 1;
    }

    private static void CheckProgressFormatting()
    {
        var mid = new InstallProgress(
            InstallPhase.Downloading, "a/b.bin", 2, 10,
            1024 * 1024, 10 * 1024 * 1024, 512 * 1024);

        Check("Fraction 落在 0..1", mid.Fraction > 0 && mid.Fraction <= 1);
        Check("Percent 落在 0..100", mid.Percent is > 0 and <= 100);
        Check("PhaseText 非空", !string.IsNullOrWhiteSpace(mid.PhaseText));
        Check("DetailText 非空", !string.IsNullOrWhiteSpace(mid.DetailText));
        Check("下载阶段的 EtaText 非空", !string.IsNullOrWhiteSpace(mid.EtaText));

        var zero = new InstallProgress(InstallPhase.Planning, null, 0, 0, 0, 0, 0);
        Check("全零进度不除零、不抛异常", zero.Fraction == 0 && zero.Percent == 0 && zero.EtaText.Length == 0);

        Check("HumanBytes 覆盖各量级",
            Hashing.HumanBytes(0) == "0 B"
            && Hashing.HumanBytes(2048).Contains("KB", StringComparison.Ordinal)
            && Hashing.HumanBytes(5L * 1024 * 1024 * 1024).Contains("GB", StringComparison.Ordinal));
        Check("HumanSpeed / HumanEta 不抛异常",
            Hashing.HumanSpeed(123456).Length > 0 && Hashing.HumanEta(TimeSpan.FromSeconds(95)).Length > 0);
    }

    /// <summary>
    /// 真的把游戏跑起来。存在的理由：.cmd/.bat 无法被 CreateProcess 直接执行，
    /// 必须经 cmd.exe /c —— 这条分支在无界面环境里点不到「启动」，只能这样验证。
    /// </summary>
    private static void CheckLaunch(GameManifest manifest, string installDir)
    {
        using var launcher = new GameLauncher();

        // ---- 1) 真实入口：证明 .cmd 被正确路由到 cmd.exe 并能起来 ----
        var record = new InstalledGame
        {
            GameId = manifest.GameId,
            Name = manifest.Name,
            Version = manifest.Version,
            InstallDir = installDir,
            Executable = manifest.Launch.Executable,
            Arguments = [.. manifest.Launch.Arguments]
        };

        RunningGame running;
        try
        {
            running = launcher.Launch(record);
        }
        catch (Exception ex)
        {
            Check($"启动 {manifest.Launch.Executable} 抛异常：{ex.Message}", false);
            return;
        }

        Check($"真实入口已启动（{manifest.Launch.Executable}，pid {running.ProcessId}）", running.ProcessId > 0);

        Thread.Sleep(1200);

        // 注意：不能断言"1.2 秒后仍在运行"。TestPack 的 start.cmd 结尾是 pause，
        // 而本自检是控制台程序、stdin 是管道，pause 读到 EOF 会立刻返回。
        // 在桌面上点「启动」时 cmd.exe 会自建交互控制台，pause 才会正常阻塞。
        launcher.Kill(manifest.GameId);
        Thread.Sleep(600);
        Check("真实入口能被结束（或已自行退出）", !launcher.IsRunning(manifest.GameId));

        // ---- 2) 保活脚本：证明脚本持续运行时进程真的处于"运行中"，且能被结束 ----
        var holdDir = Path.Combine(Path.GetTempPath(), "0verclient-smoketest", "hold");
        Directory.CreateDirectory(holdDir);
        var holdPath = Path.Combine(holdDir, "hold.cmd");

        // ping 当作 sleep 用：它不需要任何交互输入，所以在管道 stdin 下也能跑满 5 秒。
        File.WriteAllText(holdPath, "@echo off\r\nping -n 6 127.0.0.1 >nul\r\n");

        var holdRecord = new InstalledGame
        {
            GameId = "hold-check",
            Name = "hold check",
            Version = "1.0.0",
            InstallDir = holdDir,
            Executable = "hold.cmd"
        };

        RunningGame hold;
        try
        {
            hold = launcher.Launch(holdRecord);
        }
        catch (Exception ex)
        {
            Check($"启动保活脚本抛异常：{ex.Message}", false);
            return;
        }

        Check($"保活脚本已启动（pid {hold.ProcessId}）", hold.ProcessId > 0);

        Thread.Sleep(1500);
        Check("1.5 秒后进程仍在运行", launcher.IsRunning("hold-check"));
        Check("能被结束", launcher.Kill("hold-check"));

        Thread.Sleep(800);
        Check("结束后不再处于运行状态", !launcher.IsRunning("hold-check"));
    }

    private static void CheckPathGuards()
    {
        Check("拒绝路径穿越 ..", Throws(() => SafePath.NormalizeRelative("../../evil.txt")));
        Check("拒绝内嵌 ..", Throws(() => SafePath.NormalizeRelative("bin/../../evil.txt")));
        Check("拒绝绝对路径", Throws(() => SafePath.NormalizeRelative("/etc/passwd")));
        Check("拒绝盘符路径", Throws(() => SafePath.NormalizeRelative("C:/Windows/System32/x.dll")));
        Check("拒绝 UNC 路径", Throws(() => SafePath.NormalizeRelative(@"\\server\share\x.dll")));
        Check("拒绝 Windows 保留设备名", Throws(() => SafePath.NormalizeRelative("CON.txt")));
        Check("拒绝空路径", Throws(() => SafePath.NormalizeRelative("   ")));
        Check("接受正常相对路径", !Throws(() => SafePath.NormalizeRelative("bin/data/level1.pak")));
        Check("解析结果落在安装根目录内",
            SafePath.Resolve(@"C:\games\demo", "bin/a.txt").StartsWith(@"C:\games\demo\", StringComparison.OrdinalIgnoreCase));

        var strict = UrlPolicy.Strict;

        Check("https 通过", !Throws(() => strict.AssertAllowed("https://cdn.example.com/a.bin")));
        Check("白名单内的 https 通过",
            !Throws(() => new UrlPolicy(["cdn.example.com"], false).AssertAllowed("https://cdn.example.com/a.bin")));
        Check("白名单外的主机被拒",
            Throws(() => new UrlPolicy(["cdn.example.com"], false).AssertAllowed("https://evil.example.com/a.bin")));
        Check("非回环 http 被拒（默认策略）", Throws(() => strict.AssertAllowed("http://cdn.example.com/a.bin")));
        Check("回环 http 允许（本地内容源）", !Throws(() => strict.AssertAllowed("http://127.0.0.1:8787/a.bin")));
        Check("白名单非空时，回环地址仍被允许（本地调试不用改白名单）",
            !Throws(() => new UrlPolicy(["cdn.example.com"], false).AssertAllowed("http://127.0.0.1:8787/a.bin")));
        Check("非 http/https 协议被拒", Throws(() => strict.AssertAllowed("ftp://cdn.example.com/a.bin")));

        // 放行 http 的关键安全属性：开关本身不够，主机还必须被显式列出。
        var loose = new UrlPolicy(["cdn.example.com"], true);
        Check("放行 http + 主机在白名单 → 通过", !Throws(() => loose.AssertAllowed("http://cdn.example.com/a.bin")));
        Check("放行 http 但主机不在白名单 → 仍被拒",
            Throws(() => new UrlPolicy(["cdn.example.com"], true).AssertAllowed("http://evil.example.com/a.bin")));
        Check("放行 http 但白名单为空 → 仍被拒",
            Throws(() => new UrlPolicy([], true).AssertAllowed("http://cdn.example.com/a.bin")));
    }

    private static void CheckFilesMatchManifest(GameManifest manifest, string installDir)
    {
        var problems = new List<string>();

        foreach (var file in manifest.Files)
        {
            var path = SafePath.Resolve(installDir, file.Path);

            if (!File.Exists(path))
            {
                problems.Add($"缺失 {file.Path}");
                continue;
            }

            var info = new FileInfo(path);
            if (info.Length != file.Size)
            {
                problems.Add($"大小不符 {file.Path}（{info.Length} != {file.Size}）");
                continue;
            }

            var hash = Hashing.Sha256FileAsync(path).GetAwaiter().GetResult();
            if (!Hashing.Equals(hash, file.Sha256))
                problems.Add($"哈希不符 {file.Path}");
        }

        Check(
            problems.Count == 0
                ? $"磁盘内容与清单完全一致（{manifest.Files.Count} 个文件全部 sha256 校验通过）"
                : $"磁盘内容与清单不一致：{string.Join("；", problems.Take(5))}",
            problems.Count == 0);
    }

    private static bool Throws(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch
        {
            return true;
        }
    }

    private static void Check(string name, bool ok)
    {
        _checks++;

        if (ok)
        {
            Console.WriteLine($"  [通过] {name}");
            return;
        }

        _failures++;
        Console.WriteLine($"  [失败] {name}");
    }

    private static int Fail(string message)
    {
        _failures++;
        Console.WriteLine($"  [失败] {message}");
        Console.WriteLine();
        Console.WriteLine($"{_failures} 项检查失败");
        return 1;
    }

    private static string? GetArg(string[] args, string name)
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
