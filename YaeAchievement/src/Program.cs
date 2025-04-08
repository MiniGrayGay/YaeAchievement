using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Windows.Win32;
using Windows.Win32.System.Console;
using YaeAchievement.Parsers;
using YaeAchievement.res;
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

        var historyCache = GlobalVars.AchievementDataCache;

        AchievementAllDataNotify? data = null;
        try {
            data = AchievementAllDataNotify.ParseFrom(historyCache.Read().Content.ToByteArray());
        } catch (Exception) { /* ignored */ }

        if (historyCache.LastWriteTime.AddMinutes(60) > DateTime.UtcNow && data != null) {
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
    internal static unsafe void SetupConsole() {
        var handle = Native.GetStdHandle(STD_HANDLE.STD_INPUT_HANDLE);
        CONSOLE_MODE mode = default;
        Native.GetConsoleMode(handle, &mode);
        Native.SetConsoleMode(handle, mode & ~CONSOLE_MODE.ENABLE_QUICK_EDIT_MODE);
        Console.InputEncoding = Console.OutputEncoding = Encoding.UTF8;
    }

}
