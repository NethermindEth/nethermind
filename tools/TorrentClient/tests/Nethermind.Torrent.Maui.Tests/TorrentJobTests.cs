// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;

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
