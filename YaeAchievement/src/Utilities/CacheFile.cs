using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Proto;

namespace YaeAchievement.Utilities;

public static class CacheFile {

    static CacheFile() {
        // remove deprecated cache
        foreach (var file in Directory.EnumerateFiles(GlobalVars.CachePath, "*.miko")) {
            File.Delete(file);
        }
    }

    public static DateTime GetLastWriteTime(string id) {
        var fileName = Path.Combine(GlobalVars.CachePath, $"{GetStrHash(id)}.nyan");
        return File.Exists(fileName) ? File.GetLastWriteTimeUtc(fileName) : DateTime.UnixEpoch;
    }

    public static bool TryRead(string id, [NotNullWhen(true)] out CacheItem? item) {
        item = null;
        try {
            var fileName = Path.Combine(GlobalVars.CachePath, $"{GetStrHash(id)}.nyan");
            using var fileStream = File.OpenRead(fileName);
            using var zipStream = new GZipStream(fileStream, CompressionMode.Decompress);
            item = CacheItem.Parser.ParseFrom(zipStream);
            return true;
        } catch (Exception) {
            return false;
        }
    }

    public static void Write(string id, byte[] data, string? etag = null) {
        var fileName = Path.Combine(GlobalVars.CachePath, $"{GetStrHash(id)}.nyan");
        using var fileStream = File.Open(fileName, FileMode.Create);
        using var zipStream = new GZipStream(fileStream, CompressionLevel.SmallestSize);
        new CacheItem {
            Etag = etag ?? string.Empty,
            Version = 3,
            Checksum = GetBinHash(data),
            Content = ByteString.CopyFrom(data)
        }.WriteTo(zipStream);
    }

    private static string GetStrHash(string value) => GetBinHash(Encoding.UTF8.GetBytes(value));

    private static string GetBinHash(byte[] value) => Convert.ToHexStringLower(MD5.HashData(value));

}
