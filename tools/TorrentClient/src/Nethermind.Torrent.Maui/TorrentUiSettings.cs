// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;
using System.Text.Json;

namespace Nethermind.Torrent.Maui;

internal sealed class TorrentUiSettings
{
    private static readonly Guid DownloadsFolderId = new("374DE290-123F-4565-9164-39C4925E467B");

    public string DefaultDownloadDirectory { get; set; } = GetDefaultDownloadDirectory();
    public bool StartOnAdd { get; set; } = true;
    public bool AddPaused { get; set; }
    public bool VerifyExistingData { get; set; } = true;
    public bool EnableTrackers { get; set; } = true;
    public bool EnableDht { get; set; } = true;
    public int ListenPort { get; set; } = 6881;
    public bool RandomizePortOnStart { get; set; }
    public int MaxPeersPerTorrent { get; set; } = 32;
    public int MaxUploadPeers { get; set; } = 8;
    public bool SeedAfterCompletion { get; set; } = true;
    public int TrackerTimeoutSeconds { get; set; } = 20;
    public int DhtLookupIntervalSeconds { get; set; } = 90;
    public int DhtLookupTimeoutSeconds { get; set; } = 15;
    public int PeerTimeoutSeconds { get; set; } = 45;
    public bool ConfirmRemove { get; set; } = true;

    private static string GetDefaultDownloadDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }

        Guid folderId = DownloadsFolderId;
        int result = SHGetKnownFolderPath(ref folderId, 0, IntPtr.Zero, out IntPtr path);
        try
        {
            if (result == 0 && path != IntPtr.Zero && Marshal.PtrToStringUni(path) is string knownPath)
            {
                return knownPath;
            }
        }
        finally
        {
            if (path != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(path);
            }
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(ref Guid folderId, uint flags, IntPtr token, out IntPtr path);

    public TorrentClientOptions ToClientOptions(string torrentPath, string outputDirectory, bool resumeExistingData = false,
        IReadOnlyList<string>? explicitPeers = null, IReadOnlySet<int>? occupiedPorts = null, bool seedCompleted = false)
        => new()
        {
            TorrentPath = torrentPath,
            OutputDirectory = outputDirectory,
            ListenPort = SeedAfterCompletion || seedCompleted
                ? SelectListenPort(ListenPort, RandomizePortOnStart, occupiedPorts)
                : RandomizePortOnStart ? Random.Shared.Next(49152, ushort.MaxValue + 1) : ListenPort,
            MaxPeers = Math.Clamp(MaxPeersPerTorrent, 1, 512),
            MaxUploadPeers = Math.Clamp(MaxUploadPeers, 1, 64),
            SeedAfterCompletion = SeedAfterCompletion || seedCompleted,
            EnableDht = EnableDht,
            EnableTrackers = EnableTrackers,
            ExplicitPeers = explicitPeers ?? [],
            VerifyExistingData = VerifyExistingData || resumeExistingData || seedCompleted,
            TrackerTimeout = TimeSpan.FromSeconds(Math.Clamp(TrackerTimeoutSeconds, 1, 3600)),
            DhtLookupInterval = TimeSpan.FromSeconds(Math.Clamp(DhtLookupIntervalSeconds, 1, 3600)),
            DhtLookupTimeout = TimeSpan.FromSeconds(Math.Clamp(DhtLookupTimeoutSeconds, 1, 3600)),
            PeerTimeout = TimeSpan.FromSeconds(Math.Clamp(PeerTimeoutSeconds, 1, 3600)),
        };

    internal static int SelectListenPort(int preferredPort, bool randomize, IReadOnlySet<int>? occupiedPorts)
        => randomize || occupiedPorts?.Contains(preferredPort) == true ? 0 : preferredPort;

    public MagnetResolveOptions ToMagnetResolveOptions()
        => new()
        {
            EnableTrackers = EnableTrackers,
            EnableDht = EnableDht,
            ListenPort = RandomizePortOnStart ? Random.Shared.Next(49152, ushort.MaxValue + 1) : ListenPort,
            TrackerTimeout = TimeSpan.FromSeconds(Math.Clamp(TrackerTimeoutSeconds, 1, 180)),
            DhtTimeout = TimeSpan.FromSeconds(Math.Clamp(DhtLookupTimeoutSeconds, 1, 180)),
            PeerTimeout = TimeSpan.FromSeconds(Math.Clamp(PeerTimeoutSeconds, 1, 180)),
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
        => Path.Combine(TorrentAppData.DirectoryPath, SettingsFileName);

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
