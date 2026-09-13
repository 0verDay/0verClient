using OverClient.Core.Manifest;

namespace OverClient.Core.Install;

/// <summary>本地已安装记录，落盘成一个游戏一个 JSON，原子写入。</summary>
public sealed class InstalledGame
{
    public string GameId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Channel { get; set; } = "latest";
    public string InstallDir { get; set; } = "";
    public string Executable { get; set; } = "";
    public List<string> Arguments { get; set; } = [];
    public long TotalBytes { get; set; }
    public DateTimeOffset InstalledAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastPlayedAt { get; set; }
    public double PlaySeconds { get; set; }

    public TimeSpan PlayTime => TimeSpan.FromSeconds(PlaySeconds);

    public string PlayTimeText => PlaySeconds < 1
        ? "从未启动"
        : PlayTime.TotalHours >= 1
            ? $"已玩 {(int)PlayTime.TotalHours} 小时 {PlayTime.Minutes} 分"
            : $"已玩 {PlayTime.Minutes} 分 {PlayTime.Seconds} 秒";
}

public sealed class StateStore(string stateDirectory)
{
    private readonly string _directory = stateDirectory;

    private static string Sanitize(string gameId)
    {
        // gameId 来自网络，绝不能直接拼进路径。
        var safe = new string([.. gameId.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.')]);
        return string.IsNullOrEmpty(safe) ? "unknown" : safe;
    }

    public string PathFor(string gameId) => Path.Combine(_directory, Sanitize(gameId) + ".json");

    public InstalledGame? Get(string gameId)
    {
        var path = PathFor(gameId);
        if (!File.Exists(path))
            return null;

        try
        {
            var game = Manifest.JsonDefaults.Deserialize<InstalledGame>(File.ReadAllBytes(path));
            return Directory.Exists(game.InstallDir) ? game : null;
        }
        catch (Exception ex)
        {
            Log.Warn($"读取安装记录失败 {path}：{ex.Message}");
            return null;
        }
    }

    public IReadOnlyList<InstalledGame> All()
    {
        var result = new List<InstalledGame>();
        if (!Directory.Exists(_directory))
            return result;

        foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            try
            {
                var game = Manifest.JsonDefaults.Deserialize<InstalledGame>(File.ReadAllBytes(file));
                if (Directory.Exists(game.InstallDir))
                    result.Add(game);
            }
            catch (Exception ex)
            {
                Log.Warn($"跳过损坏的安装记录 {file}：{ex.Message}");
            }
        }

        return result;
    }

    public void Save(InstalledGame game)
    {
        Directory.CreateDirectory(_directory);
        var path = PathFor(game.GameId);
        var temp = path + ".tmp";

        File.WriteAllText(temp, Manifest.JsonDefaults.Serialize(game));

        if (File.Exists(path))
            File.Delete(path);

        File.Move(temp, path);
    }

    public void Remove(string gameId)
    {
        var path = PathFor(gameId);
        if (File.Exists(path))
            File.Delete(path);
    }

    public InstalledGame FromResult(InstallResult result) => new()
    {
        GameId = result.GameId,
        Name = result.Name,
        Version = result.Version,
        Channel = result.Channel,
        InstallDir = result.InstallDir,
        Executable = result.Executable,
        Arguments = [.. result.Arguments],
        TotalBytes = result.TotalBytes,
        InstalledAt = DateTimeOffset.UtcNow
    };
}
