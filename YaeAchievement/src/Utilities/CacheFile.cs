﻿using System.IO.Compression;
using Google.Protobuf;
using Proto;

namespace YaeAchievement.Utilities;

public class CacheFile(string identifier) {

    private readonly string _cacheName = Path.Combine(GlobalVars.CachePath, $"{identifier.MD5Hash()[..16]}.miko");
    private CacheItem? _content;

    public DateTime LastWriteTime => Exists() ? File.GetLastWriteTimeUtc(_cacheName) : DateTime.UnixEpoch;

    public bool Exists() => File.Exists(_cacheName);

    public CacheItem Read() {
        if (_content == null) {
            using var fInput = File.OpenRead(_cacheName);
            using var dInput = new GZipStream(fInput, CompressionMode.Decompress);
            _content = CacheItem.Parser.ParseFrom(dInput);
        }
        return _content;
    }

    public void Write(string data, string? etag = null) => Write(ByteString.CopyFromUtf8(data), data.MD5Hash(), etag);

    public void Write(byte[] data, string? etag = null) => Write(ByteString.CopyFrom(data), data.MD5Hash(), etag);

    private void Write(ByteString data, string hash, string? etag) {
        using var fOut = File.OpenWrite(_cacheName);
        using var cOut = new GZipStream(fOut, CompressionLevel.SmallestSize);
        new CacheItem {
            Etag = etag ?? string.Empty,
            Version = 3,
            Checksum = hash,
            Content = data
        }.WriteTo(cOut);
    }
}
