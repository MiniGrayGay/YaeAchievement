using System.Text.RegularExpressions;
using YaeAchievement.res;
using YaeAchievement.Utilities;

namespace YaeAchievement;

public static partial class AppConfig {

    public static string GamePath { get; private set; } = null!;

    private static readonly string[] ProductNames = [ "原神", "Genshin Impact" ];

    internal static void Load(string argumentPath) {
        if (argumentPath != "auto" && File.Exists(argumentPath)) {
            GamePath = argumentPath;
            return;
        }
        if (CacheFile.TryRead("genshin_impact_game_path", out var cache)) {
            var path = cache.Content.ToStringUtf8();
            if (path != null && File.Exists(path)) {
                GamePath = path;
                return;
            }
        }
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var logPath = ProductNames
            .Select(name => $"{appDataPath}/../LocalLow/miHoYo/{name}/output_log.txt")
            .Where(File.Exists)
            .MaxBy(File.GetLastWriteTime);
        if (logPath == null) {
            throw new ApplicationException(App.ConfigNeedStartGenshin);
        }
        GamePath = GetGamePathFromLogFile(logPath)
                   ?? GetGamePathFromLogFile($"{logPath}.last")
                   ?? throw new ApplicationException(App.ConfigNeedStartGenshin);
    }

    private static string? GetGamePathFromLogFile(string path) {
        if (!File.Exists(path)) {
            return null;
        }
        var content = File.ReadAllText(path);
        var matchResult = GamePathRegex().Match(content);
        if (!matchResult.Success) {
            return null;
        }
        var entryName = matchResult.Groups["1"].Value.Replace("_Data", ".exe");
        return Path.GetFullPath(Path.Combine(matchResult.Value, "..", entryName));
    }

    [GeneratedRegex(@"(?m).:(?:\\|/).+(GenshinImpact_Data|YuanShen_Data)", RegexOptions.IgnoreCase)]
    private static partial Regex GamePathRegex();

}
