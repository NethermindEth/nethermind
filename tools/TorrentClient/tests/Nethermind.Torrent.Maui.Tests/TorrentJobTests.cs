// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;
using System.Diagnostics;
using System.Security.Cryptography;

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
    public void Stopped_job_clears_peer_counts_and_keeps_pause_state()
    {
        TorrentJob job = new("sample.torrent", "downloads")
        {
            PieceCount = 2,
            TotalBytes = 8,
            DownloadedBytes = 4,
            KnownPeers = 7,
            ActivePeers = 2,
            Phase = "Downloading",
            Status = "Paused",
        };
        job.AttachRun(Task.CompletedTask, new CancellationTokenSource());

        job.DetachRun(completed: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(job.StageActionText, Is.EqualTo("Resume"));
            Assert.That(job.RemainingSummaryText, Is.EqualTo("4 B to complete"));
            Assert.That(job.Phase, Is.EqualTo("Paused"));
            Assert.That(job.KnownPeers, Is.Zero);
            Assert.That(job.ActivePeers, Is.Zero);
            Assert.That(job.ActiveUploadPeers, Is.Zero);
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
            Assert.That(job.CanStart, Is.True);
        }
    }

    [Test]
    public void Completed_job_shows_verified_data_instead_of_network_download_claims()
    {
        TorrentJob job = new("sample.torrent", "downloads")
        {
            PieceCount = 2,
            TotalBytes = 8,
        };
        job.Files.Add(new TorrentFileItem(new TorrentFileEntry("sample.iso", 8, 0)));
        job.BeginVerification();
        job.CompleteVerification(new TorrentVerificationProgress(2, 2, 2, 8));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(job.StageStateText, Is.EqualTo("Content verified"));
            Assert.That(job.StageActionText, Is.EqualTo("Seed"));
            Assert.That(job.VerifiedSummaryText, Is.EqualTo("8 B of 8 B verified locally"));
            Assert.That(job.RemainingSummaryText, Is.Empty);
            Assert.That(job.QueueDetailText, Is.EqualTo("8 B verified locally"));
            Assert.That(job.ProgressText, Is.EqualTo("100%"));
            Assert.That(job.OutputFolderText, Is.EqualTo("downloads"));
            Assert.That(job.NetworkReceivedText, Is.EqualTo("Not recorded"));
        }
    }

    [Test]
    public void Transfer_history_adds_run_snapshots_once_and_survives_pause()
    {
        TorrentJob job = new("sample.torrent", "downloads");
        job.RestoreTransferHistory(new TorrentTransferHistory(1000, 600, TimeSpan.FromSeconds(10).Ticks, 3, 400));
        job.AttachRun(Task.CompletedTask, new CancellationTokenSource());
        job.ApplyTransferSnapshot(new TorrentTransferSnapshot(500, 300, TimeSpan.FromSeconds(5), 2, 250, 1));
        Assert.That(job.ActiveUploadPeers, Is.EqualTo(1));
        job.ApplyTransferSnapshot(new TorrentTransferSnapshot(500, 300, TimeSpan.FromSeconds(5), 2, 250));
        job.DetachRun(completed: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(job.TransferHistory, Is.EqualTo(new TorrentTransferHistory(1500, 900, TimeSpan.FromSeconds(15).Ticks, 2, 650)));
            Assert.That(job.ActiveAverageText, Is.EqualTo("60 B/s"));
            Assert.That(job.ActiveTimeText, Is.EqualTo("15s"));
            Assert.That(job.ContributorText, Is.EqualTo("2 last run"));
            Assert.That(job.NetworkReceivedText, Is.EqualTo("1.46 KiB"));
            Assert.That(job.VerifiedFromPeersText, Is.EqualTo("900 B"));
            Assert.That(job.UploadedText, Is.EqualTo("650 B"));
            Assert.That(job.ActiveUploadPeers, Is.Zero);
        }

        job.AttachRun(Task.CompletedTask, new CancellationTokenSource());
        job.ApplyTransferSnapshot(new TorrentTransferSnapshot(300, 200, TimeSpan.FromSeconds(5), 1, 50));
        job.DetachRun(completed: true);

        Assert.That(job.TransferHistory, Is.EqualTo(new TorrentTransferHistory(1800, 1100, TimeSpan.FromSeconds(20).Ticks, 1, 700)));
    }

    [Test]
    public void Paused_seed_keeps_verified_progress_and_can_resume()
    {
        TorrentJob job = new("sample.torrent", "downloads") { TotalBytes = 8, PieceCount = 2 };
        job.CompleteVerification(new TorrentVerificationProgress(2, 2, 2, 8));
        job.AttachRun(Task.CompletedTask, new CancellationTokenSource());
        job.ApplyProgress(new TorrentSessionProgress(TorrentSessionPhase.Seeding, "sample", new string('a', 40),
            8, 8, 2, 2, 1, 1, "Seeding", DateTimeOffset.UtcNow));

        Assert.That(job.Status, Is.EqualTo("Seeding"));
        Assert.That(job.StageActionText, Is.EqualTo("Pause"));
        Assert.That(job.ResumeSeeding, Is.True);

        job.ResumeSeeding = false;
        job.Status = "Paused";
        job.DetachRun(completed: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(job.IsComplete, Is.True);
            Assert.That(job.ProgressText, Is.EqualTo("100%"));
            Assert.That(job.StageStateText, Is.EqualTo("Seeding paused"));
            Assert.That(job.StageActionText, Is.EqualTo("Resume seeding"));
            Assert.That(job.CanStart, Is.True);
        }
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task Stop_captures_undispatched_seeding_progress_and_respects_pause_intent(bool preserveSeedingIntent)
    {
        byte[] payload = "data"u8.ToArray();
        byte[] info = [.. "d6:lengthi4e4:name8:data.bin12:piece lengthi4e6:pieces20:"u8.ToArray(),
            .. SHA1.HashData(payload), (byte)'e'];
        byte[] torrent = [(byte)'d', .. "4:info"u8.ToArray(), .. info, (byte)'e'];
        string root = Path.Combine(Path.GetTempPath(), "nethermind-ui-seed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string torrentPath = Path.Combine(root, "source.torrent");
        try
        {
            await File.WriteAllBytesAsync(torrentPath, torrent);
            await File.WriteAllBytesAsync(Path.Combine(root, "data.bin"), payload);
            TorrentJob job = new(torrentPath, root);
            job.ApplyMetadata(TorrentMetadata.Load(torrentPath));
            job.CompleteVerification(new TorrentVerificationProgress(1, 1, 1, payload.Length));
            using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
            TorrentSession session = new(new TorrentClientOptions
            {
                TorrentPath = torrentPath,
                OutputDirectory = root,
                ListenPort = 0,
                EnableTrackers = false,
                EnableDht = false,
            }, _ => { });
            Task<TorrentMetadata> running = session.RunAsync(cancellation.Token);
            job.AttachRun(running, cancellation, session);
            while (session.GetLatestProgress()?.Phase != TorrentSessionPhase.Seeding)
            {
                await Task.Delay(10, cancellation.Token);
            }

            Assert.That(job.ResumeSeeding, Is.False);
            await job.StopAsync(preserveSeedingIntent);
            job.DetachRun(completed: false);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(job.IsComplete, Is.True);
                Assert.That(job.Progress, Is.EqualTo(1));
                Assert.That(job.ResumeSeeding, Is.EqualTo(preserveSeedingIntent));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void Startup_resumes_only_previously_active_fully_verified_seeds(
        [Values] bool wasSeeding, [Values] bool complete)
    {
        TorrentJob job = new("sample.torrent", "downloads") { TotalBytes = 8, PieceCount = 2, ResumeSeeding = wasSeeding };
        job.BeginVerification();

        Assert.That(job.ShouldResumeSeeding, Is.False);

        job.CompleteVerification(new TorrentVerificationProgress(2, 2, complete ? 2 : 1, complete ? 8 : 4));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(job.ShouldResumeSeeding, Is.EqualTo(wasSeeding && complete));
            Assert.That(job.ResumeSeeding, Is.EqualTo(wasSeeding && complete));
        }
    }

    [TestCase("Queued", "Start")]
    [TestCase("Ready", "Start")]
    [TestCase("Paused", "Resume")]
    [TestCase("Error", "Resume")]
    public void Context_action_matches_the_job_state(string status, string action)
    {
        TorrentJob job = new("sample.torrent", "downloads") { PieceCount = 1, Status = status };

        Assert.That(job.StageActionText, Is.EqualTo(action));
    }

    [Test]
    public void Error_summary_shows_the_reason()
    {
        TorrentJob job = new("sample.torrent", "downloads") { Status = "Error", Message = "Storage is full" };

        Assert.That(job.OverviewSummaryText, Is.EqualTo("Storage is full"));
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
