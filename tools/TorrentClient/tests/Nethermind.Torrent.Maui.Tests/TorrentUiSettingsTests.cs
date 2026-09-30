// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.Win32;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NUnit.Framework;

namespace Nethermind.Torrent.Maui.Tests;

[TestFixture]
public sealed class TorrentUiSettingsTests
{
    [Test]
    public void Default_directory_uses_windows_downloads_location()
    {
        TorrentUiSettings settings = new();
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
        string? configured = key?.GetValue("{374DE290-123F-4565-9164-39C4925E467B}") as string;
        string expected = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
            : Environment.ExpandEnvironmentVariables(configured);

        Assert.That(settings.DefaultDownloadDirectory, Is.EqualTo(expected));
    }

    [Test]
    public void Older_settings_default_to_seeding_after_completion()
    {
        TorrentUiSettings settings = JsonSerializer.Deserialize<TorrentUiSettings>("{\"StartOnAdd\":false}")!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(settings.StartOnAdd, Is.False);
            Assert.That(settings.SeedAfterCompletion, Is.True);
            Assert.That(settings.MaxUploadPeers, Is.EqualTo(8));
        }
    }

    [Test]
    public void ToClientOptions_maps_timeout_settings()
    {
        TorrentUiSettings settings = new()
        {
            TrackerTimeoutSeconds = 11,
            DhtLookupIntervalSeconds = 22,
            DhtLookupTimeoutSeconds = 33,
            PeerTimeoutSeconds = 44,
        };

        TorrentClientOptions options = settings.ToClientOptions("payload.torrent", "downloads");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(options.TrackerTimeout, Is.EqualTo(TimeSpan.FromSeconds(11)));
            Assert.That(options.DhtLookupInterval, Is.EqualTo(TimeSpan.FromSeconds(22)));
            Assert.That(options.DhtLookupTimeout, Is.EqualTo(TimeSpan.FromSeconds(33)));
            Assert.That(options.PeerTimeout, Is.EqualTo(TimeSpan.FromSeconds(44)));
            Assert.That(options.SeedAfterCompletion, Is.True);
            Assert.That(options.MaxUploadPeers, Is.EqualTo(8));
        }
    }

    [Test]
    public void Completed_job_can_seed_even_when_automatic_seeding_is_disabled()
    {
        TorrentUiSettings settings = new() { SeedAfterCompletion = false, MaxUploadPeers = 3, VerifyExistingData = false };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(settings.ToClientOptions("payload.torrent", "downloads").SeedAfterCompletion, Is.False);
            TorrentClientOptions seed = settings.ToClientOptions("payload.torrent", "downloads", seedCompleted: true);
            Assert.That(seed.SeedAfterCompletion, Is.True);
            Assert.That(seed.MaxUploadPeers, Is.EqualTo(3));
            Assert.That(seed.VerifyExistingData, Is.True);
        }
    }

    [Test]
    public void Upload_peer_limit_matches_core_bounds([Values(0, 8, 512)] int configured)
    {
        TorrentUiSettings settings = new() { MaxUploadPeers = configured };

        Assert.That(settings.ToClientOptions("payload.torrent", "downloads").MaxUploadPeers,
            Is.EqualTo(Math.Clamp(configured, 1, 64)));
    }

    [Test]
    public void Additional_seed_jobs_let_the_os_assign_a_listen_port()
    {
        TorrentUiSettings settings = new() { ListenPort = 6881 };
        TorrentClientOptions first = settings.ToClientOptions("first.torrent", "downloads");
        TorrentClientOptions additional = settings.ToClientOptions("second.torrent", "downloads", occupiedPorts: new HashSet<int> { first.ListenPort });
        settings.RandomizePortOnStart = true;
        TorrentClientOptions randomized = settings.ToClientOptions("third.torrent", "downloads");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.ListenPort, Is.EqualTo(6881));
            Assert.That(additional.ListenPort, Is.Zero);
            Assert.That(randomized.ListenPort, Is.Zero);
        }
    }

    [Test]
    public void Resuming_verified_data_requires_recheck_even_when_setting_is_disabled()
    {
        TorrentUiSettings settings = new() { VerifyExistingData = false };

        Assert.That(settings.ToClientOptions("payload.torrent", "downloads").VerifyExistingData, Is.False);
        Assert.That(settings.ToClientOptions("payload.torrent", "downloads", resumeExistingData: true).VerifyExistingData, Is.True);
    }

    [Test]
    public void ToMagnetResolveOptions_maps_discovery_settings()
    {
        TorrentUiSettings settings = new()
        {
            EnableTrackers = false,
            EnableDht = true,
            ListenPort = 12345,
            TrackerTimeoutSeconds = 11,
            DhtLookupTimeoutSeconds = 12,
            PeerTimeoutSeconds = 13,
        };

        MagnetResolveOptions options = settings.ToMagnetResolveOptions();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(options.EnableTrackers, Is.False);
            Assert.That(options.EnableDht, Is.True);
            Assert.That(options.ListenPort, Is.EqualTo(12345));
            Assert.That(options.TrackerTimeout, Is.EqualTo(TimeSpan.FromSeconds(11)));
            Assert.That(options.DhtTimeout, Is.EqualTo(TimeSpan.FromSeconds(12)));
            Assert.That(options.PeerTimeout, Is.EqualTo(TimeSpan.FromSeconds(13)));
        }
    }
}

[TestFixture]
public sealed class TorrentQueueStoreTests
{
    [Test]
    public void Queue_restores_explicit_peer_hints_without_migrating_old_entries()
    {
        string path = Path.Combine(Path.GetTempPath(), "nethermind-queue-" + Guid.NewGuid().ToString("N") + ".json");
        TorrentQueueEntry entry = new(Path.Combine(Path.GetTempPath(), "peer.torrent"), Path.GetTempPath(),
            "Peer", new string('a', 40), ["127.0.0.1:6881", "[::1]:6882"]);
        try
        {
            TorrentQueueStore.Save(path, [entry]);
            IReadOnlyList<TorrentQueueEntry> restored = TorrentQueueStore.Load(path);

            Assert.That(restored, Has.Count.EqualTo(1));
            Assert.That(restored[0].ExplicitPeers, Is.EqualTo(entry.ExplicitPeers));
            Assert.That(restored[0].Transfer, Is.Null);
            Assert.That(restored[0].ResumeSeeding, Is.False);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void Queue_round_trips_transfer_history()
    {
        string path = Path.Combine(Path.GetTempPath(), "nethermind-transfer-" + Guid.NewGuid().ToString("N") + ".json");
        TorrentQueueEntry entry = new(Path.Combine(Path.GetTempPath(), "peer.torrent"), Path.GetTempPath(),
            "Peer", new string('a', 40), Transfer: new TorrentTransferHistory(1234, 1000, 50, 2, 800), ResumeSeeding: true);
        try
        {
            TorrentQueueStore.Save(path, [entry]);
            IReadOnlyList<TorrentQueueEntry> restored = TorrentQueueStore.Load(path);

            Assert.That(restored[0].Transfer, Is.EqualTo(entry.Transfer));
            Assert.That(restored[0].ResumeSeeding, Is.True);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void Legacy_queue_without_seeding_or_upload_fields_loads_paused()
    {
        string path = Path.Combine(Path.GetTempPath(), "nethermind-legacy-" + Guid.NewGuid().ToString("N") + ".json");
        string json = JsonSerializer.Serialize(new
        {
            Version = 1,
            Jobs = new[]
            {
                new
                {
                    TorrentPath = Path.Combine(Path.GetTempPath(), "peer.torrent"),
                    OutputDirectory = Path.GetTempPath(),
                    Name = "Peer",
                    InfoHashHex = new string('a', 40),
                    Transfer = new { PayloadBytesReceived = 100L, VerifiedBytesFromPeers = 80L, ActiveTicks = 1L, LastRunContributors = 1 },
                },
            },
        });
        try
        {
            File.WriteAllText(path, json);
            TorrentQueueEntry restored = TorrentQueueStore.Load(path)[0];

            using (Assert.EnterMultipleScope())
            {
                Assert.That(restored.ResumeSeeding, Is.False);
                Assert.That(restored.Transfer?.UploadedBytes, Is.Zero);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void Resolved_metainfo_is_cached_only_after_hash_validation()
    {
        byte[] info = [.. Encoding.ASCII.GetBytes("d6:lengthi4e4:name1:a12:piece lengthi4e6:pieces20:"), .. SHA1.HashData("data"u8), (byte)'e'];
        byte[] torrent = [(byte)'d', .. Encoding.ASCII.GetBytes("4:info"), .. info, (byte)'e'];
        string infoHash = Convert.ToHexString(SHA1.HashData(info));
        string cacheDirectory = Path.Combine(Path.GetTempPath(), "nethermind-magnet-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            string cachedPath = TorrentQueueStore.CacheMetainfo(torrent, cacheDirectory, infoHash);
            Assert.That(TorrentMetadata.Load(cachedPath).InfoHashHex, Is.EqualTo(infoHash.ToLowerInvariant()));

            Assert.Throws<FormatException>(() => TorrentQueueStore.CacheMetainfo("invalid"u8.ToArray(), cacheDirectory, infoHash));
            Assert.That(File.ReadAllBytes(cachedPath), Is.EqualTo(torrent));
        }
        finally
        {
            if (Directory.Exists(cacheDirectory))
            {
                Directory.Delete(cacheDirectory, recursive: true);
            }
        }
    }

    [Test]
    public void Cached_metainfo_survives_source_removal()
    {
        string sourcePath = Path.Combine(Path.GetTempPath(), "nethermind-metainfo-" + Guid.NewGuid().ToString("N") + ".torrent");
        string infoHash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(sourcePath)));
        string cacheDirectory = Path.Combine(Path.GetTempPath(), "nethermind-metainfo-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(sourcePath, "metainfo");
            string cachedPath = TorrentQueueStore.CacheMetainfo(sourcePath, cacheDirectory, infoHash);

            File.Delete(sourcePath);

            Assert.That(File.ReadAllText(cachedPath), Is.EqualTo("metainfo"));
        }
        finally
        {
            File.Delete(sourcePath);
            if (Directory.Exists(cacheDirectory))
            {
                Directory.Delete(cacheDirectory, recursive: true);
            }
        }
    }

    [Test]
    public void Same_infohash_with_different_trackers_keeps_independent_cached_metainfo()
    {
        static byte[] WithTracker(string tracker, byte[] info)
        {
            byte[] url = Encoding.UTF8.GetBytes(tracker);
            return [(byte)'d', .. Encoding.ASCII.GetBytes($"8:announce{url.Length}:"), .. url,
                .. Encoding.ASCII.GetBytes("4:info"), .. info, (byte)'e'];
        }

        byte[] info = [.. Encoding.ASCII.GetBytes("d6:lengthi4e4:name1:a12:piece lengthi4e6:pieces20:"), .. SHA1.HashData("data"u8), (byte)'e'];
        string infoHash = Convert.ToHexString(SHA1.HashData(info));
        byte[] first = WithTracker("https://one.example/announce", info);
        byte[] second = WithTracker("https://two.example/announce", info);
        string cacheDirectory = Path.Combine(Path.GetTempPath(), "nethermind-multi-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            string firstPath = TorrentQueueStore.CacheMetainfo(first, cacheDirectory, infoHash);
            string secondPath = TorrentQueueStore.CacheMetainfo(second, cacheDirectory, infoHash);
            using (FileStream locked = new(firstPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.That(TorrentQueueStore.CacheMetainfo(firstPath, cacheDirectory, infoHash), Is.EqualTo(firstPath));
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(firstPath, Is.Not.EqualTo(secondPath));
                Assert.That(TorrentMetadata.Load(firstPath).Trackers[0].Host, Is.EqualTo("one.example"));
                Assert.That(TorrentMetadata.Load(secondPath).Trackers[0].Host, Is.EqualTo("two.example"));
                Assert.That(File.ReadAllBytes(firstPath), Is.EqualTo(first));
            }
        }
        finally
        {
            if (Directory.Exists(cacheDirectory))
            {
                Directory.Delete(cacheDirectory, recursive: true);
            }
        }
    }

    [Test]
    public void Queue_round_trips_entries_and_removal()
    {
        string path = Path.Combine(Path.GetTempPath(), "nethermind-queue-" + Guid.NewGuid().ToString("N") + ".json");
        TorrentQueueEntry first = new(
            Path.Combine(Path.GetTempPath(), "first.torrent"),
            Path.Combine(Path.GetTempPath(), "downloads-a"),
            "First",
            new string('a', 40));
        TorrentQueueEntry second = new(
            Path.Combine(Path.GetTempPath(), "second.torrent"),
            Path.Combine(Path.GetTempPath(), "downloads-b"),
            "Second",
            new string('b', 40));
        try
        {
            Assert.That(TorrentQueueStore.Load(path), Is.Empty);
            TorrentQueueStore.Save(path, [first, second]);
            Assert.That(TorrentQueueStore.Load(path), Is.EqualTo(new[] { first, second }));

            TorrentQueueStore.Save(path, [second]);
            Assert.That(TorrentQueueStore.Load(path), Is.EqualTo(new[] { second }));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".tmp");
        }
    }

    [Test]
    public void Invalid_queue_is_backed_up_before_starting_empty()
    {
        string path = Path.Combine(Path.GetTempPath(), "nethermind-queue-" + Guid.NewGuid().ToString("N") + ".json");
        string pattern = Path.GetFileName(path) + ".*.invalid";
        try
        {
            File.WriteAllText(path, "{not json");

            Assert.That(TorrentQueueStore.Load(path), Is.Empty);
            string[] backups = Directory.GetFiles(Path.GetTempPath(), pattern);
            Assert.That(backups, Has.Length.EqualTo(1));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(File.Exists(path), Is.False);
                Assert.That(File.ReadAllText(backups[0]), Is.EqualTo("{not json"));
            }
        }
        finally
        {
            File.Delete(path);
            foreach (string backup in Directory.GetFiles(Path.GetTempPath(), pattern))
            {
                File.Delete(backup);
            }
        }
    }
}
