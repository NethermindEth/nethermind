// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;

namespace Nethermind.Torrent.Maui;

internal sealed class TorrentUiSettings
{
    public string DefaultDownloadDirectory { get; set; } = Path.Combine(Environment.CurrentDirectory, "artifacts", "torrent-downloads");
    public bool StartOnAdd { get; set; } = true;
    public bool AddPaused { get; set; }
    public bool VerifyExistingData { get; set; } = true;
    public bool EnableTrackers { get; set; } = true;
    public bool EnableDht { get; set; } = true;
    public int ListenPort { get; set; } = 6881;
    public bool RandomizePortOnStart { get; set; }
    public int MaxPeersPerTorrent { get; set; } = 32;
    public int TrackerTimeoutSeconds { get; set; } = 20;
    public int DhtLookupIntervalSeconds { get; set; } = 90;
    public int DhtLookupTimeoutSeconds { get; set; } = 15;
    public int PeerTimeoutSeconds { get; set; } = 45;
    public bool ConfirmRemove { get; set; } = true;

    public TorrentClientOptions ToClientOptions(string torrentPath, string outputDirectory)
        => new()
        {
            TorrentPath = torrentPath,
            OutputDirectory = outputDirectory,
            ListenPort = RandomizePortOnStart ? Random.Shared.Next(49152, ushort.MaxValue + 1) : ListenPort,
            MaxPeers = Math.Clamp(MaxPeersPerTorrent, 1, 512),
            EnableDht = EnableDht,
            EnableTrackers = EnableTrackers,
            VerifyExistingData = VerifyExistingData,
            TrackerTimeout = TimeSpan.FromSeconds(Math.Clamp(TrackerTimeoutSeconds, 1, 3600)),
            DhtLookupInterval = TimeSpan.FromSeconds(Math.Clamp(DhtLookupIntervalSeconds, 1, 3600)),
            DhtLookupTimeout = TimeSpan.FromSeconds(Math.Clamp(DhtLookupTimeoutSeconds, 1, 3600)),
            PeerTimeout = TimeSpan.FromSeconds(Math.Clamp(PeerTimeoutSeconds, 1, 3600)),
        };
}

internal static class TorrentUiSettingsStore
{
    private const string SettingsFileName = "settings.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string? LastLoadError { get; private set; }

    public static TorrentUiSettings Load()
    {
        string path = GetSettingsPath();
        LastLoadError = null;
        if (!File.Exists(path))
        {
            return new TorrentUiSettings();
        }

        try
        {
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<TorrentUiSettings>(json, JsonOptions) ?? new TorrentUiSettings();
        }
        catch (Exception exception)
        {
            LastLoadError = "Settings reset after load failure: " + exception.Message;
            TryBackUpInvalidSettings(path);
            return new TorrentUiSettings();
        }
    }

    public static void Save(TorrentUiSettings settings)
    {
        string path = GetSettingsPath();
        string? directory = Path.GetDirectoryName(path);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(settings, JsonOptions));
    }

    private static string GetSettingsPath()
        => Path.Combine(FileSystem.AppDataDirectory, SettingsFileName);

    private static void TryBackUpInvalidSettings(string path)
    {
        try
        {
            string backupPath = path + "." + DateTimeOffset.Now.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture) + ".invalid";
            File.Move(path, backupPath, overwrite: true);
        }
        catch (Exception exception)
        {
            LastLoadError += "; backup failed: " + exception.Message;
        }
    }
}
