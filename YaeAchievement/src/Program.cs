﻿using System.Runtime.CompilerServices;
using System.Text;
using Spectre.Console;
using YaeAchievement.Parsers;
using YaeAchievement.Utilities;
using static YaeAchievement.Utils;

namespace YaeAchievement;

// TODO: WndHook

internal static class Program {

    public static async Task Main(string[] args) {

        AnsiConsole.WriteLine(@"----------------------------------------------------");
        AnsiConsole.WriteLine(App.AppBanner, GlobalVars.AppVersionName);
        AnsiConsole.WriteLine(@"https://github.com/HolographicHat/YaeAchievement");
        AnsiConsole.WriteLine(@"----------------------------------------------------");

        if (!new Mutex(true, @"Global\YaeMiku~uwu").WaitOne(0, false)) {
            AnsiConsole.WriteLine(App.AnotherInstance);
            Environment.Exit(302);
        }
        SentrySdk.Init(options => {
            options.Dsn = "https://92f11b64b0ef52cabc94f21df0428f5b@sentry.snapgenshin.com/9";
#if DEBUG
            options.Debug = true;
#endif
            options.TracesSampleRate = 1.0;
            options.AutoSessionTracking = true;
            options.SetBeforeSend(static e => {
                e.Release = GlobalVars.AppVersionName;
                return e;
            });
            options.SetBeforeSendTransaction(static e => {
                e.Release = GlobalVars.AppVersionName;
                return e;
            });
            options.CacheDirectoryPath = GlobalVars.DataPath;
        });
        InstallExitHook();
        InstallExceptionHook();

        if (GetGameProcess() != null) {
            AnsiConsole.WriteLine(App.GenshinIsRunning, 0);
            Environment.Exit(-1);
        }

        await CheckUpdate(ToBooleanOrDefault(args.GetOrNull(2)));

        AppConfig.Load(args.GetOrNull(0) ?? "auto");
        Export.ExportTo = ToIntOrDefault(args.GetOrNull(1), 114514);

        AchievementAllDataNotify? data = null;
        try {
            if (CacheFile.TryRead("achievement_data", out var cache)) {
                data = AchievementAllDataNotify.ParseFrom(cache.Content.ToByteArray());
            }
        } catch (Exception) { /* ignored */ }

        if (CacheFile.GetLastWriteTime("achievement_data").AddMinutes(60) > DateTime.UtcNow && data != null) {
            var prompt = new SelectionPromptCompat<string>()
                .Title(App.UsePreviousData)
                .AddChoices(App.CommonYes, App.CommonNo);
            if (prompt.Prompt() == App.CommonYes) {
                Export.Choose(data);
                return;
            }
        }

        StartAndWaitResult(AppConfig.GamePath, new Dictionary<int, Func<BinaryReader, bool>> {
            { 1, AchievementAllDataNotify.OnReceive },
            { 2, PlayerStoreNotify.OnReceive },
            { 100, PlayerPropNotify.OnReceive },
        }, () => {
#if DEBUG_EX
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
        FixTerminalFont();
    }

}
