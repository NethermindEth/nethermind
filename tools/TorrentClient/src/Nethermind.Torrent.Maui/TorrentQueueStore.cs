// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Json;

namespace Nethermind.Torrent.Maui;

internal sealed record TorrentQueueEntry(string TorrentPath, string OutputDirectory, string Name, string InfoHashHex);

internal static class TorrentQueueStore
{
    private const int Version = 1;
    private const int MaxEntries = 2048;
    private const int MaxFileBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private sealed record QueueDocument(int Version, List<TorrentQueueEntry> Jobs);

    public static string? LastLoadError { get; private set; }

    public static string AppPath => Path.Combine(FileSystem.AppDataDirectory, "queue.json");

    public static string AppMetainfoDirectory => Path.Combine(FileSystem.AppDataDirectory, "metainfo");

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
                    !entry.InfoHashHex.All(Uri.IsHexDigit))
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
        string cachedPath = GetCachedMetainfoPath(cacheDirectory, infoHashHex);
        if (string.Equals(sourcePath, cachedPath, StringComparison.OrdinalIgnoreCase))
        {
            return cachedPath;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(cachedPath)!);
        string temporaryPath = cachedPath + ".tmp";
        File.Copy(sourcePath, temporaryPath, overwrite: true);
        File.Move(temporaryPath, cachedPath, overwrite: true);
        return cachedPath;
    }
}
