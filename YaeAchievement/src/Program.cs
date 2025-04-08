using System.Runtime.CompilerServices;
using System.Text;
using YaeAchievement.Parsers;
using YaeAchievement.res;
using YaeAchievement.Utilities;
using static YaeAchievement.Utils;

namespace YaeAchievement;

internal static class Program {

    public static async Task Main(string[] args) {

        if (!new Mutex(true, @"Global\YaeMiku~uwu").WaitOne(0, false)) {
            Console.WriteLine(App.AnotherInstance);
            Environment.Exit(302);
        }

        InstallExitHook();
        InstallExceptionHook();

        CheckGenshinIsRunning();

        Console.WriteLine(@"----------------------------------------------------");
        Console.WriteLine(App.AppBanner, GlobalVars.AppVersionName);
        Console.WriteLine(@"https://github.com/HolographicHat/YaeAchievement");
        Console.WriteLine(@"----------------------------------------------------");

        AppConfig.Load(args.GetOrNull(0) ?? "auto");
        Export.ExportTo = ToUIntOrNull(args.GetOrNull(1)) ?? uint.MaxValue;

        await CheckUpdate(ToBooleanOrFalse(args.GetOrNull(2)));

        AchievementAllDataNotify? data = null;
        try {
            if (CacheFile.TryRead("achievement_data", out var cache)) {
                data = AchievementAllDataNotify.ParseFrom(cache.Content.ToByteArray());
            }
        } catch (Exception) { /* ignored */ }

        if (CacheFile.GetLastWriteTime("achievement_data").AddMinutes(60) > DateTime.UtcNow && data != null) {
            Console.WriteLine(App.UsePreviousData);
            if (Console.ReadLine()?.ToUpper() is "Y" or "YES") {
                Export.Choose(data);
                return;
            }
        }

        StartAndWaitResult(AppConfig.GamePath, new Dictionary<int, Func<BinaryReader, bool>> {
            { 1, AchievementAllDataNotify.OnReceive },
            { 2, PlayerStoreNotify.OnReceive },
            { 100, PlayerPropNotify.OnReceive },
        }, () => {
#if DEBUG
            PlayerPropNotify.OnFinish();
            File.WriteAllText("store_data.json", JsonSerializer.Serialize(PlayerStoreNotify.Instance, new JsonSerializerOptions {
                WriteIndented = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            }));
#endif
            AchievementAllDataNotify.OnFinish();
            Environment.Exit(0);
        });
        while (true) {}
    }

    [ModuleInitializer]
    internal static void SetupConsole() {
        SetQuickEditMode(false);
        Console.InputEncoding = Console.OutputEncoding = Encoding.UTF8;
    }

}
