// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Nethermind.Torrent.Maui;

internal sealed record TorrentQueueEntry(string TorrentPath, string OutputDirectory, string Name, string InfoHashHex, List<string>? ExplicitPeers = null);

internal static class TorrentQueueStore
{
    private const int Version = 1;
    private const int MaxEntries = 2048;
    private const int MaxFileBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private sealed record QueueDocument(int Version, List<TorrentQueueEntry> Jobs);

    public static string? LastLoadError { get; private set; }

    public static string AppPath => Path.Combine(TorrentAppData.DirectoryPath, "queue.json");

    public static string AppMetainfoDirectory => Path.Combine(TorrentAppData.DirectoryPath, "metainfo");

    public static IReadOnlyList<TorrentQueueEntry> Load(string path)
    {
        LastLoadError = null;
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            if (new FileInfo(path).Length > MaxFileBytes)
            {
                throw new FormatException("Torrent queue file is too large.");
            }

            QueueDocument document = JsonSerializer.Deserialize<QueueDocument>(File.ReadAllText(path), JsonOptions)
                ?? throw new FormatException("Torrent queue file is empty.");
            if (document.Version != Version)
            {
                throw new InvalidDataException($"Unsupported torrent queue version {document.Version}.");
            }

            if (document.Jobs is null || document.Jobs.Count > MaxEntries)
            {
                throw new FormatException("Torrent queue contains an invalid number of entries.");
            }

            foreach (TorrentQueueEntry entry in document.Jobs)
            {
                if (entry is null ||
                    string.IsNullOrWhiteSpace(entry.TorrentPath) || !Path.IsPathFullyQualified(entry.TorrentPath) ||
                    string.IsNullOrWhiteSpace(entry.OutputDirectory) || !Path.IsPathFullyQualified(entry.OutputDirectory) ||
                    string.IsNullOrWhiteSpace(entry.Name) || entry.InfoHashHex?.Length != 40 ||
                    !entry.InfoHashHex.All(Uri.IsHexDigit) ||
                    entry.ExplicitPeers is { Count: > 64 } ||
                    entry.ExplicitPeers?.Any(peer => string.IsNullOrWhiteSpace(peer) || peer.Length > 300) == true)
                {
                    throw new FormatException("Torrent queue contains an invalid entry.");
                }
            }

            return document.Jobs;
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            string backupPath = path + "." + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture) + ".invalid";
            File.Move(path, backupPath);
            LastLoadError = "Invalid torrent queue was backed up to " + backupPath;
            return [];
        }
    }

    public static void Save(string path, IReadOnlyList<TorrentQueueEntry> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count > MaxEntries)
        {
            throw new InvalidOperationException("Torrent queue is full.");
        }

        string json = JsonSerializer.Serialize(new QueueDocument(Version, [.. entries]), JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > MaxFileBytes)
        {
            throw new InvalidOperationException("Torrent queue file would be too large.");
        }

        string? directory = Path.GetDirectoryName(path);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, path, overwrite: true);
    }

    public static string GetCachedMetainfoPath(string cacheDirectory, string infoHashHex)
    {
        if (infoHashHex.Length != 40 || !infoHashHex.All(Uri.IsHexDigit))
        {
            throw new FormatException("Invalid torrent info hash.");
        }

        return Path.Combine(cacheDirectory, infoHashHex.ToLowerInvariant() + ".torrent");
    }

    public static string CacheMetainfo(string sourcePath, string cacheDirectory, string infoHashHex)
    {
        _ = GetCachedMetainfoPath(cacheDirectory, infoHashHex);
        if (File.Exists(sourcePath) && IsContentAddressedPath(sourcePath, cacheDirectory, infoHashHex))
        {
            return sourcePath;
        }

        byte[] contents = File.ReadAllBytes(sourcePath);
        string cachedPath = GetContentAddressedPath(cacheDirectory, infoHashHex, contents);
        if (string.Equals(sourcePath, cachedPath, StringComparison.OrdinalIgnoreCase))
        {
            return cachedPath;
        }

        Directory.CreateDirectory(cacheDirectory);
        string temporaryPath = cachedPath + ".tmp";
        try
        {
            File.WriteAllBytes(temporaryPath, contents);
            File.Move(temporaryPath, cachedPath, overwrite: true);
            return cachedPath;
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    public static string CacheMetainfo(byte[] torrentBytes, string cacheDirectory, string infoHashHex)
    {
        ArgumentNullException.ThrowIfNull(torrentBytes);
        string cachedPath = GetContentAddressedPath(cacheDirectory, infoHashHex, torrentBytes);
        Directory.CreateDirectory(cacheDirectory);
        string temporaryPath = cachedPath + ".tmp";
        try
        {
            File.WriteAllBytes(temporaryPath, torrentBytes);
            TorrentMetadata metadata = TorrentMetadata.Load(temporaryPath);
            if (!string.Equals(metadata.InfoHashHex, infoHashHex, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Resolved magnet metadata has the wrong info hash.");
            }

            File.Move(temporaryPath, cachedPath, overwrite: true);
            return cachedPath;
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static string GetContentAddressedPath(string cacheDirectory, string infoHashHex, ReadOnlySpan<byte> contents)
    {
        string legacyPath = GetCachedMetainfoPath(cacheDirectory, infoHashHex);
        string digest = Convert.ToHexString(SHA256.HashData(contents)).ToLowerInvariant();
        return Path.Combine(cacheDirectory, Path.GetFileNameWithoutExtension(legacyPath) + "-" + digest + ".torrent");
    }

    private static bool IsContentAddressedPath(string sourcePath, string cacheDirectory, string infoHashHex)
    {
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(sourcePath)), Path.GetFullPath(cacheDirectory),
            StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string fileName = Path.GetFileName(sourcePath);
        string prefix = infoHashHex + "-";
        if (fileName.Length != prefix.Length + 64 + ".torrent".Length ||
            !fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !fileName.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        for (int i = prefix.Length; i < prefix.Length + 64; i++)
        {
            if (!Uri.IsHexDigit(fileName[i]))
            {
                return false;
            }
        }

        return true;
    }
}
