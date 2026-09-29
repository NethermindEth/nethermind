// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;
using System.Diagnostics;

namespace Nethermind.Torrent.Maui.Tests;

[TestFixture]
public sealed class TorrentJobTests
{
    [Test]
    public void Queued_activity_is_bounded_and_drained_in_batches()
    {
        TorrentJob job = new("sample.torrent", "downloads");
        for (int i = 0; i < 230; i++)
        {
            job.QueueLog($"line {i}");
        }

        job.DrainLogs(24);

        Assert.That(job.LogLines, Has.Count.EqualTo(24));
        Assert.That(job.LogLines[0], Does.EndWith("line 30"));

        job.DrainLogs(300);

        Assert.That(job.LogLines, Has.Count.EqualTo(200));
        Assert.That(job.LogLines[^1], Does.EndWith("line 229"));
    }

    [Test]
    public void Rendered_activity_keeps_only_recent_lines()
    {
        TorrentJob job = new("sample.torrent", "downloads");
        for (int i = 0; i < 450; i++)
        {
            job.AppendLog($"line {i}");
        }

        Assert.That(job.LogLines, Has.Count.EqualTo(400));
        Assert.That(job.LogLines[0], Does.EndWith("line 50"));
    }

    [Test]
    public void Stopped_job_clears_live_transfer_indicators()
    {
        TorrentJob job = new("sample.torrent", "downloads")
        {
            DownloadRateBytesPerSecond = 1024,
            ActivePeers = 3,
        };
        job.AttachRun(Task.CompletedTask, new CancellationTokenSource());
        job.DownloadRateBytesPerSecond = 2048;

        job.DetachRun(completed: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(job.DownloadRateBytesPerSecond, Is.Zero);
            Assert.That(job.ActivePeers, Is.Zero);
            Assert.That(job.IsRunning, Is.False);
        }
    }

    [Test]
    public void Verified_existing_data_restores_paused_progress_and_completion()
    {
        TorrentJob job = new("sample.torrent", "downloads")
        {
            PieceCount = 2,
            TotalBytes = 8,
        };
        job.BeginVerification();
        job.CompleteVerification(new TorrentVerificationProgress(2, 2, 1, 4));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(job.Status, Is.EqualTo("Paused"));
            Assert.That(job.Progress, Is.EqualTo(0.5));
            Assert.That(job.DownloadedBytes, Is.EqualTo(4));
            Assert.That(job.CanStart, Is.True);
        }

        job.BeginVerification();
        job.CompleteVerification(new TorrentVerificationProgress(2, 2, 2, 8));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(job.Status, Is.EqualTo("Complete"));
            Assert.That(job.Progress, Is.EqualTo(1));
            Assert.That(job.CanStart, Is.False);
        }
    }

    [Test]
    public void Transient_progress_reset_does_not_disable_resume_verification()
    {
        TorrentJob job = new("sample.torrent", "downloads") { DownloadedBytes = 4 };

        job.DownloadedBytes = 0;

        Assert.That(job.HasDataToResume, Is.True);
    }

    [TestCase("payload.iso", "payload.iso")]
    [TestCase("collection\\disc1.iso", "collection")]
    public void Payload_path_points_to_torrent_file_or_top_level_directory(string filePath, string expectedName)
    {
        string outputDirectory = Path.Combine(Path.GetTempPath(), "torrent-output");
        TorrentJob job = new("source.torrent", outputDirectory);
        job.Files.Add(new TorrentFileItem(new TorrentFileEntry(filePath, 1, 0)));

        Assert.That(job.PayloadPath, Is.EqualTo(Path.Combine(outputDirectory, expectedName)));
    }

    [Test]
    public void File_search_matches_relative_paths_without_case_or_surrounding_space()
    {
        TorrentJob job = new("source.torrent", Path.GetTempPath());
        TorrentFileItem first = new(new TorrentFileEntry(Path.Combine("Slackware", "boot", "kernel"), 1, 0));
        TorrentFileItem second = new(new TorrentFileEntry(Path.Combine("Slackware", "README"), 1, 1));
        job.Files.Add(first);
        job.Files.Add(second);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(job.FindFiles(" BOOT "), Is.EqualTo(new[] { first }));
            Assert.That(job.FindFiles("readme"), Is.EqualTo(new[] { second }));
            Assert.That(job.FindFiles("slackware"), Is.EqualTo(new[] { first, second }));
            Assert.That(job.FindFiles("missing"), Is.Empty);
            Assert.That(job.FindFiles(" "), Is.SameAs(job.Files));
        }
    }

    [Test]
    public void File_reveal_path_stays_inside_the_download_directory()
    {
        string root = Path.Combine(Path.GetTempPath(), "torrent-output");
        TorrentJob job = new("source.torrent", root);
        TorrentFileItem valid = new(new TorrentFileEntry(Path.Combine("album", "disc.iso"), 1, 0));
        TorrentFileItem escaping = new(new TorrentFileEntry(Path.Combine("..", "outside.iso"), 1, 0));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(job.ResolveFilePath(valid), Is.EqualTo(Path.Combine(root, "album", "disc.iso")));
            Assert.That(() => job.ResolveFilePath(escaping), Throws.InvalidOperationException);
        }
    }

    [Test]
    public void Copied_magnet_retains_explicit_peers_and_stays_importable_with_many_trackers()
    {
        TorrentJob job = new("source.torrent", Path.GetTempPath())
        {
            InfoHashHex = new string('a', 40),
            Name = "Example",
        };
        job.ExplicitPeers.Add("127.0.0.1:6881");
        for (int i = 0; i < 1000; i++)
        {
            job.Trackers.Add($"https://tracker-{i}.example/announce");
        }

        MagnetLink parsed = MagnetLink.Parse(job.MagnetUri);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(job.MagnetUri.Length, Is.LessThanOrEqualTo(MagnetLink.MaxUriLength));
            Assert.That(parsed.InfoHashHex, Is.EqualTo(job.InfoHashHex));
            Assert.That(parsed.ExplicitPeers, Is.EqualTo(new[] { "127.0.0.1:6881" }));
            Assert.That(parsed.Trackers, Is.Not.Empty);
        }
    }
}

[TestFixture]
public sealed class TorrentFileRevealTests
{
    [Test]
    public void Windows_reveal_selects_files_and_opens_directories()
    {
        ProcessStartInfo file = TorrentFileReveal.CreateWindowsStartInfo(@"C:\Downloads\disc one.iso", isFile: true);
        ProcessStartInfo directory = TorrentFileReveal.CreateWindowsStartInfo(@"C:\Downloads\album one", isFile: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(file.FileName, Is.EqualTo("explorer.exe"));
            Assert.That(file.Arguments, Is.EqualTo("/select,\"C:\\Downloads\\disc one.iso\""));
            Assert.That(directory.Arguments, Is.EqualTo("\"C:\\Downloads\\album one\""));
            Assert.That(file.UseShellExecute, Is.False);
        }
    }

    [Test]
    public void Linux_reveal_uses_file_manager_dbus_and_fallback_directory_openers()
    {
        ProcessStartInfo select = TorrentFileReveal.CreateLinuxSelectStartInfo(new Uri("file:///home/user/album/disc%20one,part.iso"));
        ProcessStartInfo xdg = TorrentFileReveal.CreateLinuxOpenStartInfo("xdg-open", "/home/user/album one");
        ProcessStartInfo gio = TorrentFileReveal.CreateLinuxOpenStartInfo("gio", "/home/user/album one");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(select.FileName, Is.EqualTo("dbus-send"));
            Assert.That(select.ArgumentList, Does.Contain("array:string:file:///home/user/album/disc%20one%2Cpart.iso"));
            Assert.That(xdg.ArgumentList, Is.EqualTo(new[] { "/home/user/album one" }));
            Assert.That(gio.ArgumentList, Is.EqualTo(new[] { "open", "/home/user/album one" }));
        }
    }
}
