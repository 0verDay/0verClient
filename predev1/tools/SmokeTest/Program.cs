using System.Diagnostics;
using System.Text;
using OverClient.Core;
using OverClient.Core.Install;
using OverClient.Core.Launch;
using OverClient.Core.Manifest;
using OverClient.Core.Net;
using OverClient.Core.Update;
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

        // ---- 启动器自更新的两个"扮演"模式 ----
        //
        // 这段放在最前面，而且刻意做在 SmokeTest 里而不是新建一个工程：
        // 要验证替换逻辑，必须有一个**能被复制、能被重新拉起的真 exe**，
        // 而 SmokeTest 本身就是。Core 里的 LauncherUpdater 被两边共用，
        // 所以这里跑通的就是启动器里跑的同一份代码。

        // 扮演"新版启动器"：被旧版用 --apply-update 拉起，完成替换。
        var apply = LauncherUpdater.ParseApplyArgs(args);
        if (apply is not null)
        {
            Log.Init();
            return LauncherUpdater.ApplyUpdate(apply.Value.TargetExe, apply.Value.ParentPid, TimeSpan.FromSeconds(20));
        }

        // 扮演"旧版本仍在运行"：占住自己的 exe 不放，用来验证更新器真的会等。
        if (GetArg(args, "--sleep") is { } secondsText && int.TryParse(secondsText, out var seconds))
        {
            Console.WriteLine($"hold-exe pid={Environment.ProcessId} for {seconds}s");
            Thread.Sleep(TimeSpan.FromSeconds(seconds));
            return 0;
        }

        // 替换完成后目标会被重新拉起 —— 那个子进程继承了这个环境变量，立刻退出，
        // 免得它在无人看管的情况下真的跑一遍完整自检。
        if (Environment.GetEnvironmentVariable("OVERCLIENT_UPDATETEST_RELAUNCH") == "1")
        {
            Console.WriteLine("RELAUNCHED-BY-UPDATER");
            return 0;
        }

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

        // latest.json 是**可选**文件：站点提供就得解析出来，不提供就必须安静地降级为 null。
        // 它绝不能让"刷新游戏库"失败 —— 所以两条路都要在这里钉一次。
        var release = await manifests.LoadLauncherReleaseAsync(indexUrl);

        if (release is null)
            Check("站点没有 latest.json → 安静降级为「无更新」，不抛异常", true);
        else
            Check($"latest.json 可读（站点最新启动器 v{release.Version}，{Hashing.HumanBytes(release.Size)}）",
                !string.IsNullOrWhiteSpace(release.Version) && !string.IsNullOrWhiteSpace(release.Sha256));

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
        Console.WriteLine("[7] 更新判定（决定玩家到底能不能拿到新版本）");
        CheckUpdateLogic(entry, second);

        if (args.Contains("--update-check", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine();
            Console.WriteLine("[8] 启动器自替换（真的拷自己、真的等旧进程退出）");
            CheckLauncherSelfReplace();
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine("[8] 已跳过启动器自替换检查（加 --update-check 会真的做一次替换）");
        }

        Console.WriteLine();
        Console.WriteLine(_failures == 0
            ? $"全部通过（{_checks} 项检查）"
            : $"{_failures} / {_checks} 项检查失败");

        return _failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// 更新判定。前半段是纯逻辑（不需要网络），后半段拿**刚才真实装出来的**记录去对真实索引。
    ///
    /// 为什么值得单独一段：这条逻辑错了的症状是"我明明重传了，玩家却一直不更新"，
    /// 在界面上完全看不出哪里不对，只能靠这些边界情况挡住。
    /// </summary>
    private static void CheckUpdateLogic(GameEntry entry, InstallResult installed)
    {
        var record = new InstalledGame { GameId = "g", Version = "1.0.0", ManifestSha256 = "AAA" };

        Check("同版本同哈希 → 无更新", !UpdateCheck.IsGameUpdateAvailable(record, Channel("1.0.0", "AAA")));
        Check("版本更高 → 有更新", UpdateCheck.IsGameUpdateAvailable(record, Channel("1.0.1", "AAA")));
        Check("版本跨段更高 → 有更新", UpdateCheck.IsGameUpdateAvailable(record, Channel("2.0.0", "AAA")));
        Check("本地版本更高 → 无更新（不倒退）", !UpdateCheck.IsGameUpdateAvailable(record, Channel("0.9.0", "BBB")));

        // 这一条是这次改动的核心理由：发布者忘记改版本号时，玩家仍然拿得到新内容。
        Check("同版本但清单哈希变了 → 有更新", UpdateCheck.IsGameUpdateAvailable(record, Channel("1.0.0", "BBB")));

        // 老版本写下的 state 文件里没有 sha，此时必须退化成"只比版本号"，而不是误报成永远有更新。
        var legacy = new InstalledGame { GameId = "g", Version = "1.0.0", ManifestSha256 = null };
        Check("老记录没有 sha → 同版本不误报", !UpdateCheck.IsGameUpdateAvailable(legacy, Channel("1.0.0", "BBB")));

        Check("尚未安装 → 不走更新（走安装）", !UpdateCheck.IsGameUpdateAvailable(null, Channel("1.0.0", "AAA")));
        Check("索引里没有可用通道 → 无更新", !UpdateCheck.IsGameUpdateAvailable(record, null));

        // ---- 启动器自身 ----
        Check("站点没有 latest.json → 无更新", !UpdateCheck.CheckLauncher("0.1.0", null).Available);
        Check("站点版本与当前相同 → 无更新", !UpdateCheck.CheckLauncher("0.2.0", Release("0.2.0", null)).Available);
        Check("站点版本比当前旧 → 无更新", !UpdateCheck.CheckLauncher("0.2.0", Release("0.1.0", null)).Available);
        Check("版本号为空 → 无更新", !UpdateCheck.CheckLauncher("0.1.0", Release("", null)).Available);

        var newer = UpdateCheck.CheckLauncher("0.1.0", Release("0.2.0", null));
        Check("站点版本更新 → 有更新且非强制", newer.Available && !newer.Mandatory);
        Check("有 url + sha256 → 允许一键更新", newer.CanAutoApply);

        var mandatory = UpdateCheck.CheckLauncher("0.1.0", Release("0.3.0", "0.2.0"));
        Check("当前低于 minVersion → 强制更新", mandatory.Available && mandatory.Mandatory);

        var atMin = UpdateCheck.CheckLauncher("0.2.0", Release("0.3.0", "0.2.0"));
        Check("当前正好等于 minVersion → 不强制", atMin.Available && !atMin.Mandatory);

        var noHash = UpdateCheck.CheckLauncher("0.1.0", Release("0.2.0", null, sha: null));
        Check("缺少 sha256 → 禁止一键更新，只提示", noHash.Available && !noHash.CanAutoApply);

        // ---- 真实链路：刚装完的游戏，对着同一份索引不应该报"有更新" ----
        var stateDir = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(installed.InstallDir)!)!, "state");
        var store = new StateStore(stateDir);
        store.Save(store.FromResult(installed));
        var reloaded = store.Get(installed.GameId);

        Check("安装记录里写下了清单 sha256", !string.IsNullOrWhiteSpace(reloaded?.ManifestSha256));
        Check("刚装完 → 立刻检查不会误报有更新",
            !UpdateCheck.IsGameUpdateAvailable(reloaded, entry.ChannelFor("latest")));
    }

    private static ChannelEntry Channel(string version, string? manifestSha) => new()
    {
        Version = version,
        ManifestSha256 = manifestSha,
        ManifestUrl = "http://example.invalid/games/g/manifest.json"
    };

    private static LauncherRelease Release(string version, string? minVersion, string? sha = "DEADBEEF") => new()
    {
        SchemaVersion = LauncherRelease.CurrentSchema,
        Version = version,
        MinVersion = minVersion,
        Url = "http://example.invalid/client/0verClient.exe",
        Sha256 = sha,
        Size = 1234
    };

    /// <summary>
    /// 端到端验证"新版启动器替换旧版"这条路径。
    ///
    /// 拓扑必须和真实更新**完全一致**，否则测的就是别的东西：
    ///   installed\  ——  "已安装的旧版本"，它正在运行（所以占着自己的 exe）
    ///   incoming\   ——  "新版"，从另一个目录被拉起，去替换 installed 里的那个
    ///
    /// 注意两件事：
    ///   1) 两个目录都要放**完整输出**，不能只拷 exe ——
    ///      这些 exe 是 apphost，旁边没有同名 dll 就起不来（第一次写这个测试就踩了）；
    ///   2) 改脏的必须是 installed 里那个，因为"替换是否真的发生"要靠它区分。
    ///
    /// 最关键的断言是耗时：旧进程还活着时 Windows 不允许覆盖它的 exe，
    /// 所以更新器**必须**等到 ~4 秒（holder 的存活时间）之后才成功。
    /// 不等就会立刻失败 —— 这正是自更新最容易写错、且只在真机上暴露的地方。
    /// </summary>
    private static void CheckLauncherSelfReplace()
    {
        var self = LauncherUpdater.CurrentExecutablePath;
        var root = Path.Combine(Path.GetTempPath(), "0verclient-updatetest", Guid.NewGuid().ToString("N")[..8]);
        var installedDir = Path.Combine(root, "installed");
        var incomingDir = Path.Combine(root, "incoming");

        try
        {
            Check("能拿到真实 exe 路径（不是单文件解包的临时目录）",
                File.Exists(self) && Path.GetExtension(self).Equals(".exe", StringComparison.OrdinalIgnoreCase));

            CopyDirectory(Path.GetDirectoryName(self)!, installedDir);
            CopyDirectory(Path.GetDirectoryName(self)!, incomingDir);

            var target = Path.Combine(installedDir, Path.GetFileName(self));
            var incoming = Path.Combine(incomingDir, Path.GetFileName(self));

            var cleanHash = Hashing.Sha256FileAsync(incoming).GetAwaiter().GetResult();

            // 把"已安装"的那份改脏：不然新旧字节一样，"替换成功"和"根本没动"无法区分。
            using (var stream = new FileStream(target, FileMode.Append, FileAccess.Write))
                stream.WriteByte(0x00);

            Check("准备：目标已被改脏（否则无法区分替换是否真的发生）",
                !Hashing.Equals(Hashing.Sha256FileAsync(target).GetAwaiter().GetResult(), cleanHash));

            // "旧版本正在运行" —— 它占住的正是 target 这个文件。
            var holder = Process.Start(new ProcessStartInfo
            {
                FileName = target,
                Arguments = "--sleep 4",
                UseShellExecute = false
            })!;

            Thread.Sleep(700);   // 等它起来并锁住自己的 exe

            var started = DateTime.UtcNow;

            var updaterInfo = new ProcessStartInfo
            {
                FileName = incoming,
                Arguments = $"{LauncherUpdater.ApplyFlag} \"{target}\" {holder.Id}",
                UseShellExecute = false
            };

            // 替换完成后 target 会被重新拉起；那个孙子进程继承这个变量后立刻退出，
            // 免得它在后台真跑一遍完整自检。只给更新器设，不影响本进程。
            updaterInfo.Environment["OVERCLIENT_UPDATETEST_RELAUNCH"] = "1";

            var updater = Process.Start(updaterInfo)!;
            var finished = updater.WaitForExit(30_000);
            var elapsed = DateTime.UtcNow - started;

            holder.WaitForExit(15_000);

            Check("更新器在 30 秒内结束", finished);

            if (!finished)
            {
                try { updater.Kill(); } catch { /* 已经没了 */ }
                return;
            }

            Check($"更新器退出码为 0（实际 {updater.ExitCode}）", updater.ExitCode == 0);

            var afterHash = Hashing.Sha256FileAsync(target).GetAwaiter().GetResult();
            Check("目标 exe 的字节已被替换成新版", Hashing.Equals(afterHash, cleanHash));

            Check($"替换发生在旧进程退出之后（耗时 {elapsed.TotalSeconds:0.0}s，应 ≥ 3s）",
                elapsed.TotalSeconds >= 3.0);
        }
        catch (Exception ex)
        {
            Check($"自替换过程抛异常：{ex.GetType().Name}: {ex.Message}", false);
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch
            {
                // 被重新拉起的那个进程可能还占着文件，清理失败不该让自检变红。
            }
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
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
