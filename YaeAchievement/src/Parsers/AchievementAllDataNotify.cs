﻿using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Protobuf;
using Spectre.Console;
using YaeAchievement.Utilities;

namespace YaeAchievement.Parsers;

public enum AchievementStatus {
    Invalid,
    Unfinished,
    Finished,
    RewardTaken,
}

public sealed class AchievementItem {
    
    public uint Id { get; init; }
    public uint TotalProgress { get; init; }
    public uint CurrentProgress { get; init; }
    public uint FinishTimestamp { get; init; }
    public AchievementStatus Status { get; init; }
    
}

public sealed class AchievementAllDataNotify {

    public List<AchievementItem> AchievementList { get; private init; } = [];
    
    private static AchievementAllDataNotify? Instance { get; set; }

    public static bool OnReceive(BinaryReader reader) {
        var bytes = reader.ReadBytes();
        CacheFile.Write("achievement_data", bytes);
        Instance = ParseFrom(bytes);
        return true;
    }

    public static void OnFinish() {
        if (Instance == null) {
            throw new ApplicationException("No data received");
        }
        Export.Choose(Instance);
    }

    public static AchievementAllDataNotify ParseFrom(byte[] bytes) {
        using var stream = new CodedInputStream(bytes);
        var data = new List<Dictionary<uint, uint>>();
        var errTimes = 0;
        try {
            uint tag;
            while ((tag = stream.ReadTag()) != 0) {
                if ((tag & 7) == 2) { // is LengthDelimited
                    var dict = new Dictionary<uint, uint>();
                    using var eStream = stream.ReadLengthDelimitedAsStream();
                    try {
                        while ((tag = eStream.ReadTag()) != 0) {
                            if ((tag & 7) != 0) { // not VarInt
                                dict = null;
                                break;
                            }
                            dict[tag >> 3] = eStream.ReadUInt32();
                        }
                        if (dict is { Count: > 2 }) { // at least 3 fields
                            data.Add(dict);
                        }
                    } catch (InvalidProtocolBufferException) {
                        if (errTimes++ > 0) { // allows 1 fail on 'reward_taken_goal_id_list'
                            throw;
                        }
                    }
                }
            }
        } catch (InvalidProtocolBufferException) {
            // ReSharper disable once LocalizableElement
            AnsiConsole.WriteLine("Parse failed");
            File.WriteAllBytes("achievement_raw_data.bin", bytes);
            Environment.Exit(0);
        }
        if (data.Count == 0) {
            return new AchievementAllDataNotify();
        }
        uint tId, sId, iId, currentId, totalId;
        if (data.All(CheckKnownFieldIdIsValid)) {
            var info = GlobalVars.AchievementInfo.PbInfo;
            iId = info.Id;
            tId = info.FinishTimestamp;
            sId = info.Status;
            totalId = info.TotalProgress;
            currentId = info.CurrentProgress;
        } else if (data.Count > 20) {
            (tId, var cnt) = data //        ↓ 2020-09-15 04:15:14
                .GroupKeys(value => value > 1600114514).Select(g => (g.Key, g.Count())).MaxBy(p => p.Item2);
            sId = data //           FINISHED ↓    ↓ REWARD_TAKEN
                .GroupKeys(value => value is 2 or 3).First(g => g.Count() == cnt).Key;
            iId = data //                                 ↓ id: 8xxxx
                .GroupKeys(value => value / 10000 % 10 == 8).MaxBy(g => g.Count())!.Key;
            (currentId, totalId) = data
                .Where(d => d[sId] is 2 or 3)
                .Select(d => d.ToDictionary().RemoveValues(tId, sId, iId).ToArray())
                .Where(d => d.Length == 2 && d[0].Value != d[1].Value)
                .GroupBy(a => a[0].Value > a[1].Value ? (a[0].Key, a[1].Key) : (a[1].Key, a[0].Key))
                .Select(g => (FieldIds: g.Key, Count: g.Count()))
                .MaxBy(p => p.Count)
                .FieldIds;
#if DEBUG
            // ReSharper disable once LocalizableElement
            AnsiConsole.WriteLine($"Id={iId}, Status={sId}, Total={totalId}, Current={currentId}, Timestamp={tId}");
#endif
        } else {
            AnsiConsole.WriteLine(App.WaitMetadataUpdate);
            Environment.Exit(0);
            return null!;
        }
        return new AchievementAllDataNotify {
            AchievementList = data.Select(dict => new AchievementItem {
                Id = dict[iId],
                Status = (AchievementStatus) dict[sId],
                TotalProgress = dict[totalId],
                CurrentProgress = dict.GetValueOrDefault(currentId),
                FinishTimestamp = dict.GetValueOrDefault(tId),
            }).ToList()
        };
        // ReSharper disable once ConvertIfStatementToSwitchStatement
        static bool CheckKnownFieldIdIsValid(Dictionary<uint, uint> data) {
            var info = GlobalVars.AchievementInfo;
            var status = data.GetValueOrDefault(info.PbInfo.Status, 114514u);
            if (status is 0 or > 3) {
                return false;
            }
            if (status > 1 && data.GetValueOrDefault(info.PbInfo.FinishTimestamp) < 1600114514) { // 2020-09-15 04:15:14
                return false;
            }
            return info.Items.ContainsKey(data.GetValueOrDefault(info.PbInfo.Id));
        }
    }

}

[JsonSerializable(typeof(AchievementAllDataNotify))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    GenerationMode = JsonSourceGenerationMode.Serialization,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower
)]
public sealed partial class AchievementRawDataSerializer : JsonSerializerContext {

    public static string Serialize(AchievementAllDataNotify ntf) {
        return JsonSerializer.Serialize(ntf, Default.AchievementAllDataNotify);
    }
}
