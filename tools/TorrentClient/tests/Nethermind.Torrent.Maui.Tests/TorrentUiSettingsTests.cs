// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.Win32;
using System.Security.Cryptography;
using System.Text;
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
        }
    }

    [Test]
    public void Resuming_verified_data_requires_recheck_even_when_setting_is_disabled()
    {
        TorrentUiSettings settings = new() { VerifyExistingData = false };

        Assert.That(settings.ToClientOptions("payload.torrent", "downloads").VerifyExistingData, Is.False);
        Assert.That(settings.ToClientOptions("payload.torrent", "downloads", resumeExistingData: true).VerifyExistingData, Is.True);
    }
}

[TestFixture]
public sealed class TorrentQueueStoreTests
{
    [Test]
    public void Cached_metainfo_survives_source_removal()
    {
        string sourcePath = Path.Combine(Path.GetTempPath(), "nethermind-metainfo-" + Guid.NewGuid().ToString("N") + ".torrent");
        string infoHash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(sourcePath)));
        string cacheDirectory = Path.Combine(Path.GetTempPath(), "nethermind-metainfo-cache-" + Guid.NewGuid().ToString("N"));
        string cachedPath = TorrentQueueStore.GetCachedMetainfoPath(cacheDirectory, infoHash);
        try
        {
            File.WriteAllText(sourcePath, "metainfo");
            Assert.That(TorrentQueueStore.CacheMetainfo(sourcePath, cacheDirectory, infoHash), Is.EqualTo(cachedPath));

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
