// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.IO.Abstractions;
using System.Net;
using System.Security.Cryptography;
using Nethermind.Api;
using Nethermind.Core.Test;
using Nethermind.Core.Test.IO;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;
using Testably.Abstractions;

namespace Nethermind.Init.Snapshot.Test;

public class InitDatabaseSnapshotTests
{
    private const int SnapshotPayloadSize = 100_000;

    private TempPath _tempDir = null!;
    private string _dbPath = null!;
    private string _snapshotPath = null!;
    private SnapshotConfig _snapshotConfig = null!;
    private INethermindApi _api = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = TempPath.GetTempDirectory();
        string snapshotDirectory = Path.Combine(_tempDir.Path, "snapshot");
        Directory.CreateDirectory(snapshotDirectory);
        _dbPath = Path.Combine(_tempDir.Path, "db");
        _snapshotConfig = new SnapshotConfig
        {
            Enabled = true,
            DownloadUrl = "http://127.0.0.1:1/snapshot.tar",
            SnapshotDirectory = snapshotDirectory,
            SnapshotFileName = "snapshot.tar",
            StripComponents = 1
        };
        _snapshotPath = Path.Combine(snapshotDirectory, _snapshotConfig.SnapshotFileName);

        _api = Substitute.For<INethermindApi>();
        _api.Config<ISnapshotConfig>().Returns(_snapshotConfig);
        _api.Config<IInitConfig>().Returns(new InitConfig { BaseDbPath = _dbPath });
        _api.LogManager.Returns(LimboLogs.Instance);
        _api.FileSystem.Returns(new RealFileSystem());
    }

    [TearDown]
    public void TearDown() => _tempDir.Dispose();

    [Test]
    public async Task Execute_FreeSpaceCoversExtractionButNotLegacyMultiplier_ExtractsSnapshot()
    {
        long snapshotSize = WriteSnapshotTar();
        AdvanceCheckpoint(SnapshotStage.Verified);
        long freeSpace = snapshotSize * 2;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(freeSpace, Is.GreaterThanOrEqualTo(SnapshotDiskSpace.GetRequiredSpaceForExtraction(snapshotSize)),
                "precondition: free space must cover the extraction estimate");
            Assert.That(freeSpace, Is.LessThan((long)(snapshotSize * 2.5)),
                "precondition: free space must be below the legacy 2.5x requirement to prove the regression is fixed");
        }
        InitDatabaseSnapshot step = new(_api, DrivesWithFreeSpace(freeSpace));

        await step.Execute(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.Exists(Path.Combine(_dbPath, "state.bin")), Is.True,
                "the snapshot content should be extracted into the database directory");
            Assert.That(File.Exists(_snapshotPath), Is.False,
                "the snapshot archive should be deleted after a successful extraction");
        }
    }

    [Test]
    public async Task Execute_FreeSpaceBelowExtractionEstimate_ThrowsIOException()
    {
        long snapshotSize = WriteSnapshotTar();
        AdvanceCheckpoint(SnapshotStage.Verified);
        InitDatabaseSnapshot step = new(_api, DrivesWithFreeSpace(snapshotSize));

        IOException exception = (await Assert.ThrowsAsync<IOException>(() => step.Execute(CancellationToken.None)))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception.Message, Does.Contain("Insufficient disk space"),
                "the extraction should be rejected when free space is below the extraction estimate");
            Assert.That(Directory.Exists(_dbPath), Is.False,
                "nothing should be extracted when free space is insufficient");
        }
    }

    [Test]
    public async Task Execute_FreeSpaceBelowDownloadRequirement_ThrowsIOExceptionBeforeDownloading()
    {
        using SnapshotServer server = SnapshotServer.Start(contentLength: 1_000_000);
        _snapshotConfig.DownloadUrl = server.Url;
        InitDatabaseSnapshot step = new(_api, DrivesWithFreeSpace(1_000_000));

        IOException exception = (await Assert.ThrowsAsync<IOException>(() => step.Execute(CancellationToken.None)))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception.Message, Does.Contain("Insufficient disk space"),
                "the download should be rejected when free space cannot fit the snapshot and its extraction");
            Assert.That(File.Exists(_snapshotPath), Is.False,
                "no bytes should be downloaded when free space is insufficient");
        }
    }

    [Test]
    public async Task Execute_StreamingEnabled_ExtractsSnapshotFromStream()
    {
        using FlakySnapshotServer server = new();
        Dictionary<string, byte[]> files = TestArchive.BuildFiles();
        byte[] archive = TestArchive.BuildTarZst(files);
        server.Content = archive;
        _snapshotConfig.SnapshotFileName = "snapshot.tar.zst";
        _snapshotConfig.DownloadUrl = server.Url;
        _snapshotConfig.Streaming = true;
        _snapshotConfig.Checksum = Convert.ToHexString(SHA256.HashData(archive));
        InitDatabaseSnapshot step = new(_api, DrivesWithFreeSpace(archive.Length * 2L));

        await step.Execute(CancellationToken.None);

        Assert.That(File.Exists(Path.Combine(_dbPath, "state/a42.sst")), Is.True,
            "the streaming path must extract the snapshot into the database directory");
    }

    [TestCase(0, TestName = "Zero")]
    [TestCase(17, TestName = "AboveMaximum")]
    public async Task Execute_StreamingConnectionsOutOfRange_Throws(int connections)
    {
        _snapshotConfig.Streaming = true;
        _snapshotConfig.StreamingConnections = connections;
        InitDatabaseSnapshot step = new(_api, DrivesWithFreeSpace(long.MaxValue));
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));

        await Assert.ThrowsAsync<InvalidOperationException>(() => step.Execute(timeout.Token),
            "a connection count outside 1-16 must be rejected before any network activity");
    }

    [Test]
    public async Task Execute_VerifiedCheckpointWithoutArchive_StreamsFromScratch()
    {
        using FlakySnapshotServer server = new();
        Dictionary<string, byte[]> files = TestArchive.BuildFiles();
        byte[] archive = TestArchive.BuildTarZst(files);
        server.Content = archive;
        _snapshotConfig.SnapshotFileName = "snapshot.tar.zst";
        AdvanceCheckpoint(SnapshotStage.Verified);
        _snapshotConfig.DownloadUrl = server.Url;
        _snapshotConfig.Streaming = true;
        _snapshotConfig.Checksum = Convert.ToHexString(SHA256.HashData(archive));
        InitDatabaseSnapshot step = new(_api, DrivesWithFreeSpace(archive.Length * 2L));

        await step.Execute(CancellationToken.None);

        Assert.That(File.Exists(Path.Combine(_dbPath, "state/a42.sst")), Is.True,
            "a checkpoint past Downloaded with no archive on disk must stream instead of skipping the download stage");
    }

    [TestCase(nameof(SnapshotStage.Verified), TestName = "Verified")]
    [TestCase(nameof(SnapshotStage.Extracted), TestName = "Extracted")]
    [TestCase(nameof(SnapshotStage.Completed), TestName = "Completed")]
    public async Task Execute_CheckpointPastDownloadWithoutArchiveOrDatabase_RedownloadsAndExtracts(string stage)
    {
        using FlakySnapshotServer server = new();
        server.Content = TestArchive.BuildTar();
        AdvanceCheckpoint(Enum.Parse<SnapshotStage>(stage));
        _snapshotConfig.DownloadUrl = server.Url;
        InitDatabaseSnapshot step = new(_api, DrivesWithFreeSpace(long.MaxValue));

        await step.Execute(CancellationToken.None);

        Assert.That(File.Exists(Path.Combine(_dbPath, "state.bin")), Is.True,
            "a checkpoint past the download with neither archive nor database must restart the download instead of failing startup forever");
    }

    [Test]
    public async Task Execute_StreamingEnabledWithDownloadedArchive_ExtractsWithoutStreaming()
    {
        WriteSnapshotTar();
        AdvanceCheckpoint(SnapshotStage.Verified);
        _snapshotConfig.Streaming = true;
        InitDatabaseSnapshot step = new(_api, DrivesWithFreeSpace(long.MaxValue));

        await step.Execute(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.Exists(Path.Combine(_dbPath, "state.bin")), Is.True,
                "an already downloaded archive must be extracted through the two-phase path instead of being deleted and re-streamed");
            Assert.That(File.Exists(_snapshotPath), Is.False,
                "the archive must be deleted after a successful two-phase extraction");
        }
    }

    [Test]
    public async Task Execute_CompletedCheckpointButEmptyDatabaseDirectory_ReinitializesFromSnapshot()
    {
        using FlakySnapshotServer server = new();
        Dictionary<string, byte[]> files = TestArchive.BuildFiles();
        byte[] archive = TestArchive.BuildTarZst(files);
        server.Content = archive;
        Directory.CreateDirectory(Path.Combine(_dbPath, "lost+found"));
        _snapshotConfig.SnapshotFileName = "snapshot.tar.zst";
        AdvanceCheckpoint(SnapshotStage.Completed);
        _snapshotConfig.DownloadUrl = server.Url;
        _snapshotConfig.Streaming = true;
        _snapshotConfig.Checksum = Convert.ToHexString(SHA256.HashData(archive));
        InitDatabaseSnapshot step = new(_api, DrivesWithFreeSpace(long.MaxValue));

        await step.Execute(CancellationToken.None);

        Assert.That(File.Exists(Path.Combine(_dbPath, "state/a42.sst")), Is.True,
            "a stale completed checkpoint with a missing or empty database must reinitialize instead of skipping and leaving the node to sync from genesis");
    }

    [Test]
    public async Task Execute_ArchiveNotMatchingStripComponents_Throws()
    {
        File.WriteAllBytes(_snapshotPath, TestArchive.BuildTarWithoutTopLevelDirectory());
        AdvanceCheckpoint(SnapshotStage.Verified);
        InitDatabaseSnapshot step = new(_api, DrivesWithFreeSpace(long.MaxValue));

        InvalidOperationException exception = (await Assert.ThrowsAsync<InvalidOperationException>(() => step.Execute(CancellationToken.None)))!;

        Assert.That(exception.Message, Does.Contain("StripComponents"),
            "an extraction that produced no files must point the operator at the strip configuration");
    }

    [Test]
    public async Task Execute_ExtractedCheckpointWithArchivePresentButNoDatabase_ReusesArchive()
    {
        WriteSnapshotTar();
        AdvanceCheckpoint(SnapshotStage.Extracted);
        _snapshotConfig.Streaming = true;
        InitDatabaseSnapshot step = new(_api, DrivesWithFreeSpace(long.MaxValue));

        await step.Execute(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.Exists(Path.Combine(_dbPath, "state.bin")), Is.True,
                "a complete archive left by a crash between extraction and completion must be reused instead of re-downloaded");
            Assert.That(File.Exists(_snapshotPath), Is.False,
                "the archive must be deleted after the successful extraction");
        }
    }

    [Test]
    public async Task Execute_RelativeDatabasePath_ExtractsSnapshot()
    {
        // The default BaseDbPath is relative to the working directory, which is how most nodes run.
        _api.Config<IInitConfig>().Returns(new InitConfig { BaseDbPath = Path.GetRelativePath(Environment.CurrentDirectory, _dbPath) });
        WriteSnapshotTar();
        AdvanceCheckpoint(SnapshotStage.Verified);
        InitDatabaseSnapshot step = new(_api, DrivesWithFreeSpace(long.MaxValue));

        await step.Execute(CancellationToken.None);

        Assert.That(File.Exists(Path.Combine(_dbPath, "state.bin")), Is.True);
    }

    [TestCase(false, TestName = "Execute_EntryEscapingAbsoluteDatabasePath_ThrowsIOException")]
    [TestCase(true, TestName = "Execute_EntryEscapingRelativeDatabasePath_ThrowsIOException")]
    public async Task Execute_EntryEscapingDatabasePath_ThrowsIOException(bool relativeDbPath)
    {
        if (relativeDbPath)
            _api.Config<IInitConfig>().Returns(new InitConfig { BaseDbPath = Path.GetRelativePath(Environment.CurrentDirectory, _dbPath) });
        File.WriteAllBytes(_snapshotPath, TestArchive.BuildTarEscapingDestination());
        AdvanceCheckpoint(SnapshotStage.Verified);
        InitDatabaseSnapshot step = new(_api, DrivesWithFreeSpace(long.MaxValue));

        IOException exception = (await Assert.ThrowsAsync<IOException>(() => step.Execute(CancellationToken.None)))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception.Message, Does.Contain("outside the destination directory"));
            Assert.That(File.Exists(Path.Combine(_tempDir.Path, "escaped.bin")), Is.False);
        }
    }

    [Test]
    public async Task Execute_NonEmptyDatabaseWithoutMarker_KeepsItAndSkipsTheSnapshot(
        [Values(null, "Started", "Downloaded", "Verified", "NotAStage", "Extracted", "Completed")] string? checkpoint,
        [Values] bool streaming)
    {
        using FlakySnapshotServer server = new();
        byte[] archive = TestArchive.BuildTarZst(TestArchive.BuildFiles());
        server.Content = archive;
        _snapshotConfig.SnapshotFileName = "snapshot.tar.zst";
        _snapshotConfig.DownloadUrl = server.Url;
        _snapshotConfig.Streaming = streaming;
        _snapshotConfig.Checksum = Convert.ToHexString(SHA256.HashData(archive));
        if (checkpoint is not null)
            File.WriteAllText(Path.Combine(_snapshotConfig.SnapshotDirectory, $"checkpoint_{_snapshotConfig.SnapshotFileName}"), checkpoint);
        // A database the node synced itself, or one whose checkpoint went with a changed or lost snapshot directory.
        string synced = Path.Combine(_dbPath, "state", "000001.sst");
        Directory.CreateDirectory(Path.GetDirectoryName(synced)!);
        File.WriteAllBytes(synced, [1, 2, 3]);
        TestLogger logger = new();
        _api.LogManager.Returns(new OneLoggerLogManager(new(logger)));

        await new InitDatabaseSnapshot(_api, DrivesWithFreeSpace(long.MaxValue)).Execute(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.Exists(synced), Is.True, "a database without the extraction marker is not the snapshot's to delete");
            Assert.That(server.RequestCount, Is.Zero, "the snapshot must be skipped, not downloaded over the kept database");
            Assert.That(logger.LogList.Any(static line => line.Contains("was not written by an unfinished snapshot extraction")),
                Is.EqualTo(checkpoint is not ("Extracted" or "Completed")),
                "a kept database must be reported with the way to get the snapshot; a completed one is skipped as before");
        }
    }

    [TestCase(nameof(SnapshotStage.Verified), TestName = "Execute_InterruptedExtraction_CleansAndExtractsAgain")]
    [TestCase(nameof(SnapshotStage.Extracted), TestName = "Execute_ExtractionStoppedBeforeRemovingItsMarker_CleansAndExtractsAgain")]
    [TestCase(nameof(SnapshotStage.Completed), TestName = "Execute_ExtractionCompletedBeforeRemovingItsMarker_CleansAndExtractsAgain")]
    public async Task Execute_UnfinishedExtraction_CleansAndExtractsAgain(string checkpointOnRestart)
    {
        InitDatabaseSnapshot step = new(_api, DrivesWithFreeSpace(long.MaxValue));
        await FailExtraction(step);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.Exists(MarkerPath), Is.True, "the extraction must mark the database as its own before writing to it");
            Assert.That(File.Exists(Path.Combine(_dbPath, "state", "a42.sst")), Is.True, "precondition: the failed extraction left files behind");
        }

        Dictionary<string, byte[]> files = TestArchive.BuildFiles(seed: 43);
        File.WriteAllBytes(_snapshotPath, TestArchive.BuildTar(files));
        // Extracted, Completed: the node stopped after the checkpoint advanced and before the marker was removed; the
        // two-phase path removes it after Extracted, the streaming one after Completed, and admission is the same for both.
        AdvanceCheckpoint(Enum.Parse<SnapshotStage>(checkpointOnRestart));

        await step.Execute(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.ReadAllBytes(Path.Combine(_dbPath, "state", "a43.sst")), Is.EqualTo(files["state/a43.sst"]),
                "the restart must extract the archive again");
            Assert.That(File.Exists(Path.Combine(_dbPath, "state", "a42.sst")), Is.False, "the restart must delete what the unfinished extraction wrote");
            Assert.That(File.Exists(MarkerPath), Is.False, "a finished extraction must remove its marker");
            Assert.That(ReadCheckpoint(), Is.EqualTo(SnapshotStage.Completed));
        }
    }

    [Test]
    public async Task Execute_FileWithMarkerNameButOtherContent_ThrowsAndKeepsTheDatabase()
    {
        string synced = Path.Combine(_dbPath, "000001.sst");
        Directory.CreateDirectory(_dbPath);
        File.WriteAllBytes(synced, [1, 2, 3]);
        File.WriteAllText(MarkerPath, "not a marker");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));

        IOException exception = (await Assert.ThrowsAsync<IOException>(
            () => new InitDatabaseSnapshot(_api, DrivesWithFreeSpace(long.MaxValue)).Execute(timeout.Token)))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception.Message, Does.Contain("extraction marker"), "the failure must name the file that blocks the start");
            Assert.That(File.Exists(synced), Is.True, "a file that only has the marker's name proves nothing, so nothing may be deleted");
        }
    }

    [TestCase(1, TestName = "Execute_AfterStreamedExtractionWasInterrupted_CleansAndStreamsAgain")]
    [TestCase(2, TestName = "Execute_AfterSecondStreamingAttemptWasInterrupted_CleansAndStreamsAgain")]
    public async Task Execute_AfterStreamedExtractionWasInterrupted_CleansAndStreamsAgain(int interruptedAttempt)
    {
        using FlakySnapshotServer server = new();
        // Uncompressed, so every chunk reaches the extractor as soon as it arrives.
        byte[] archive = TestArchive.BuildTar(TestArchive.BuildFiles(seed: 42));
        int earlierRequests = 0;
        if (interruptedAttempt == 1)
        {
            server.Content = archive;
        }
        else
        {
            // The source changes at the second chunk: after the probe and two chunk requests the first attempt is
            // cleaned up, and the second attempt streams the new archive.
            server.Content = TestArchive.BuildTar(TestArchive.BuildFiles(seed: 41));
            server.SwitchSourceAfterRequests(2, archive, "\"v2\"");
            earlierRequests = 3;
        }

        _snapshotConfig.DownloadUrl = server.Url;
        _snapshotConfig.Streaming = true;
        const int chunkSize = 4096;
        // The probe, then one request per chunk. With one connection the last chunk is requested only after all chunks
        // but the last two were consumed, so the first file is extracted by then.
        int lastChunkRequest = earlierRequests + 1 + (archive.Length + chunkSize - 1) / chunkSize;
        TaskCompletionSource lastChunkRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int requests = 0;
        server.BeforeResponse = async token =>
        {
            if (Interlocked.Increment(ref requests) != lastChunkRequest)
                return;
            lastChunkRequested.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token); // held until the server stops
        };
        StreamingSnapshotInitializer initializer = new(_snapshotConfig, server.Url, _dbPath, [],
            new SnapshotStreamSettings(1, chunkSize, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(30)),
            LimboLogs.Instance);
        using CancellationTokenSource stop = new();

        Task interrupted = initializer.InitializeAsync(new SnapshotCheckpoint(_snapshotConfig, LimboLogs.Instance), stop.Token);
        try
        {
            await lastChunkRequested.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            stop.Cancel(); // As Ctrl+C would; it also ends the stream when the request never came.
        }

        await Assert.CatchAsync<OperationCanceledException>(() => interrupted.WaitAsync(TimeSpan.FromSeconds(30)),
            "precondition: the stream must stop when the node stops");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.Exists(MarkerPath), Is.True, "every streamed attempt must mark the database as its own before writing to it");
            Assert.That(File.Exists(Path.Combine(_dbPath, "state", "a42.sst")), Is.True, "precondition: the interrupted stream left files behind");
        }

        Dictionary<string, byte[]> files = TestArchive.BuildFiles(seed: 43);
        server.SwitchSourceAfterRequests(0, TestArchive.BuildTar(files), "\"v3\"");
        using CancellationTokenSource restartTimeout = new(TimeSpan.FromSeconds(60));

        await new InitDatabaseSnapshot(_api, DrivesWithFreeSpace(long.MaxValue)).Execute(restartTimeout.Token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.ReadAllBytes(Path.Combine(_dbPath, "state", "a43.sst")), Is.EqualTo(files["state/a43.sst"]),
                "the restart must stream the snapshot again");
            Assert.That(File.Exists(Path.Combine(_dbPath, "state", "a42.sst")), Is.False, "the restart must delete what the interrupted stream wrote");
            Assert.That(File.Exists(MarkerPath), Is.False, "a finished extraction must remove its marker");
            Assert.That(ReadCheckpoint(), Is.EqualTo(SnapshotStage.Completed));
        }
    }

    [Test]
    public async Task Execute_StreamedSnapshotFailedThenNodeSynced_KeepsTheSyncedDatabase()
    {
        using FlakySnapshotServer server = new();
        server.Content = TestArchive.BuildTarZst(TestArchive.BuildFiles());
        _snapshotConfig.SnapshotFileName = "snapshot.tar.zst";
        _snapshotConfig.DownloadUrl = server.Url;
        _snapshotConfig.Streaming = true;
        _snapshotConfig.Checksum = Convert.ToHexString(SHA256.HashData([9, 9, 9]));
        InitDatabaseSnapshot step = new(_api, DrivesWithFreeSpace(long.MaxValue));

        await step.Execute(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(SnapshotDatabase.Exists(_dbPath), Is.False, "precondition: the checksum failed and the extraction was cleaned up");
            Assert.That(File.Exists(MarkerPath), Is.False, "a cleaned-up attempt must give the database back to the node");
        }

        string synced = Path.Combine(_dbPath, "state", "000001.sst");
        Directory.CreateDirectory(Path.GetDirectoryName(synced)!);
        File.WriteAllBytes(synced, [1, 2, 3]);
        int requests = server.RequestCount;

        await step.Execute(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.Exists(synced), Is.True, "the database the node synced after the failed snapshot must survive the next start");
            Assert.That(server.RequestCount, Is.EqualTo(requests), "the snapshot must not be retried over the synced database");
        }
    }

    [TestCase(true, TestName = "Execute_LinkedDatabaseDirectory_RestartedExtractionKeepsTheLink")]
    [TestCase(false, TestName = "Execute_LinkedSubdirectory_RestartedExtractionKeepsTheLink")]
    [Platform(Include = "Linux")] // creating links needs a privilege on Windows
    public async Task Execute_LinkedLayout_RestartedExtractionKeepsTheLink(bool linkDatabaseDirectory)
    {
        // The link points at another disk that holds only lost+found, as a freshly mounted disk does.
        string target = Path.Combine(_tempDir.Path, "other", linkDatabaseDirectory ? "db" : "state");
        string keep = Path.Combine(target, "lost+found", "keep");
        Directory.CreateDirectory(Path.GetDirectoryName(keep)!);
        File.WriteAllBytes(keep, [1]);
        string link = linkDatabaseDirectory ? _dbPath : Path.Combine(_dbPath, "state");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        Directory.CreateSymbolicLink(link, target);
        string stateOnTarget = linkDatabaseDirectory ? Path.Combine(target, "state") : target;
        InitDatabaseSnapshot step = new(_api, DrivesWithFreeSpace(long.MaxValue));

        await FailExtraction(step);
        Assert.That(File.Exists(Path.Combine(stateOnTarget, "a42.sst")), Is.True,
            "an empty linked layout is a fresh database, so the extraction must write through the link");

        File.WriteAllBytes(_snapshotPath, TestArchive.BuildTar(TestArchive.BuildFiles(seed: 43)));
        await step.Execute(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(new DirectoryInfo(link).LinkTarget, Is.EqualTo(target), "the cleanup must keep the link");
            Assert.That(File.Exists(Path.Combine(stateOnTarget, "a43.sst")), Is.True, "the restarted extraction must land on the linked disk");
            Assert.That(File.Exists(Path.Combine(stateOnTarget, "a42.sst")), Is.False, "the cleanup must empty the link target");
            Assert.That(File.Exists(keep), Is.True, "lost+found on the linked disk must survive the cleanup");
        }
    }

    [TestCase(true, false, TestName = "Execute_LinkedDatabaseDirectoryWithMissingTarget_StopsAndKeepsTheLink")]
    [TestCase(false, false, TestName = "Execute_LinkedSubdirectoryWithMissingTarget_StopsAndKeepsTheLink")]
    [TestCase(true, true, TestName = "Execute_LinkedDatabaseDirectoryInLoop_StopsAndKeepsTheLink")]
    [TestCase(false, true, TestName = "Execute_LinkedSubdirectoryInLoop_StopsAndKeepsTheLink")]
    [Platform(Include = "Linux")] // creating links needs a privilege on Windows
    public async Task Execute_LinkThatDoesNotResolve_StopsAndKeepsTheLink(bool linkDatabaseDirectory, bool loop)
    {
        string target = Path.Combine(_tempDir.Path, "other", linkDatabaseDirectory ? "db" : "state");
        Directory.CreateDirectory(target);
        string link = linkDatabaseDirectory ? _dbPath : Path.Combine(_dbPath, "state");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        Directory.CreateSymbolicLink(link, target);
        InitDatabaseSnapshot step = new(_api, DrivesWithFreeSpace(long.MaxValue));
        await FailExtraction(step);
        // The linked disk is not mounted at the next start, or its mount point now leads back to the link.
        string unmounted = target + "-unmounted";
        Directory.Move(target, unmounted);
        if (loop)
            Directory.CreateSymbolicLink(target, link);
        File.WriteAllBytes(_snapshotPath, TestArchive.BuildTar(TestArchive.BuildFiles(seed: 43)));

        IOException exception = (await Assert.CatchAsync<IOException>(() => step.Execute(CancellationToken.None)))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception.Message, Does.Contain("missing or cannot be read"), "the start must stop at a link it cannot follow");
            Assert.That(new DirectoryInfo(link).LinkTarget, Is.EqualTo(target), "the link must be neither removed nor replaced by a plain directory");
            Assert.That(File.Exists(Path.Combine(unmounted, linkDatabaseDirectory ? "state/a42.sst" : "a42.sst")), Is.True,
                "the content behind the link must stay for the cleanup after the disk is back");
            Assert.That(File.Exists(linkDatabaseDirectory ? Path.Combine(unmounted, Path.GetFileName(MarkerPath)) : MarkerPath), Is.True,
                "the marker must stay, so the cleanup runs once the disk is back");
        }
    }

    [Test]
    [Platform(Include = "Linux")] // creating links needs a privilege on Windows
    public async Task Execute_ContentOnlyInLinkedSubdirectoryWithoutMarker_KeepsIt()
    {
        using FlakySnapshotServer server = new();
        server.Content = TestArchive.BuildTarZst(TestArchive.BuildFiles());
        _snapshotConfig.SnapshotFileName = "snapshot.tar.zst";
        _snapshotConfig.DownloadUrl = server.Url;
        _snapshotConfig.Streaming = true;
        string target = Path.Combine(_tempDir.Path, "other", "state");
        string synced = Path.Combine(target, "000001.sst");
        Directory.CreateDirectory(target);
        File.WriteAllBytes(synced, [1, 2, 3]);
        Directory.CreateDirectory(_dbPath);
        Directory.CreateSymbolicLink(Path.Combine(_dbPath, "state"), target);

        await new InitDatabaseSnapshot(_api, DrivesWithFreeSpace(long.MaxValue)).Execute(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.Exists(synced), Is.True, "files behind a linked subdirectory are database content");
            Assert.That(server.RequestCount, Is.Zero, "the snapshot must not be streamed over a database held on a linked disk");
        }
    }

    [Test]
    [Platform(Include = "Linux")] // creating links needs a privilege on Windows
    public async Task Execute_DirectoryLinkInsideUnfinishedExtraction_StopsWithoutRemovingOrFollowingIt()
    {
        InitDatabaseSnapshot step = new(_api, DrivesWithFreeSpace(long.MaxValue));
        await FailExtraction(step);
        string target = Path.Combine(_tempDir.Path, "elsewhere");
        Directory.CreateDirectory(target);
        File.WriteAllBytes(Path.Combine(target, "keep"), [1]);
        string link = Path.Combine(_dbPath, "state", "linked");
        Directory.CreateSymbolicLink(link, target);

        IOException exception = (await Assert.CatchAsync<IOException>(() => step.Execute(CancellationToken.None)))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception.Message, Does.Contain("directory link"),
                "an extraction never creates a directory link, so the cleanup must stop at one instead of guessing");
            Assert.That(new DirectoryInfo(link).LinkTarget, Is.EqualTo(target), "the cleanup must not remove a directory link it did not create");
            Assert.That(File.Exists(Path.Combine(target, "keep")), Is.True, "the cleanup must not follow a directory link it did not create");
            Assert.That(File.Exists(MarkerPath), Is.True, "a cleanup that stopped must keep the marker, so the next start cleans up again");
        }
    }

    [Test]
    [Platform(Include = "Linux")] // creating links needs a privilege on Windows
    public async Task Execute_FileLinksInsideUnfinishedExtraction_AreDeletedAsLinks()
    {
        // The database directory is a link, so a relative ../ link inside it resolves on its target's disk.
        string disk = Path.Combine(_tempDir.Path, "disk");
        string target = Path.Combine(disk, "db");
        Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(_dbPath, target);
        InitDatabaseSnapshot step = new(_api, DrivesWithFreeSpace(long.MaxValue));
        await FailExtraction(step);
        string outside = Path.Combine(disk, "x");
        File.WriteAllBytes(outside, [1]);
        File.CreateSymbolicLink(Path.Combine(target, "top"), Path.Combine("..", "x"));
        File.CreateSymbolicLink(Path.Combine(target, "state", "nested"), Path.Combine("..", "..", "x"));
        // Links to files the cleanup deletes too: whichever of a pair comes first, the link is not taken for a missing one.
        // Half the pairs are created file first and half link first, so in most listing orders some file comes before its link.
        for (int i = 0; i < 8; i++)
        {
            if (i % 2 == 1)
                File.CreateSymbolicLink(Path.Combine(target, $"z{i}"), $"z{i}-real");
            File.WriteAllBytes(Path.Combine(target, $"z{i}-real"), [1]);
            if (i % 2 == 0)
                File.CreateSymbolicLink(Path.Combine(target, $"z{i}"), $"z{i}-real");
        }

        File.WriteAllBytes(_snapshotPath, TestArchive.BuildTar(TestArchive.BuildFiles(seed: 43)));
        await step.Execute(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.Exists(Path.Combine(target, "state", "a43.sst")), Is.True, "healthy file links must not stop the restart");
            Assert.That(Directory.EnumerateFileSystemEntries(target).Select(Path.GetFileName), Is.EquivalentTo(new[] { "state", "headers" }),
                "the file links and the files of the unfinished extraction must be gone");
            Assert.That(File.Exists(outside), Is.True, "a file link is deleted as a link, never followed");
        }
    }

    [TestCase(false, TestName = "Execute_SecondStartDuringAnExtraction_StopsAtOnceAndLetsTheFirstFinish")]
    [TestCase(true, TestName = "Execute_SecondStartDuringARestartedExtraction_StopsAtOnceAndLetsTheFirstFinish")]
    public async Task Execute_SecondStartDuringAnExtraction_StopsAtOnceAndLetsTheFirstFinish(bool restarted)
    {
        // Restarted: the first start cleans up an unfinished extraction before it streams.
        if (restarted)
            await FailExtraction(new InitDatabaseSnapshot(_api, DrivesWithFreeSpace(long.MaxValue)));
        using FlakySnapshotServer server = new();
        Dictionary<string, byte[]> files = TestArchive.BuildFiles(seed: 43);
        byte[] archive = TestArchive.BuildTarZst(files);
        server.Content = archive;
        _snapshotConfig.SnapshotFileName = "snapshot.tar.zst";
        _snapshotConfig.DownloadUrl = server.Url;
        _snapshotConfig.Streaming = true;
        _snapshotConfig.Checksum = Convert.ToHexString(SHA256.HashData(archive));
        // The probe, then the archive: the first start is held at the archive request, after it marked the database.
        TaskCompletionSource extracting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int requests = 0;
        server.BeforeResponse = async token =>
        {
            if (Interlocked.Increment(ref requests) != 2)
                return;
            extracting.TrySetResult();
            await resume.Task.WaitAsync(token);
        };
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));
        Task first = new InitDatabaseSnapshot(_api, DrivesWithFreeSpace(long.MaxValue)).Execute(timeout.Token);
        await extracting.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Without the lock, the second start would see the first one's marker. Had it waited the restart delay, the first
        // start would finish meanwhile, and the second would then delete the finished database.
        Task second = new InitDatabaseSnapshot(_api, DrivesWithFreeSpace(long.MaxValue)).Execute(timeout.Token);
        Assert.That(File.Exists(MarkerPath), Is.True, "the second start must not delete what the first one is extracting");
        resume.SetResult();
        await first;

        IOException exception = (await Assert.ThrowsAsync<IOException>(() => second))!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception.Message, Does.Contain("Another process is initializing the database"), "the second start must stop at the first one's lock");
            Assert.That(File.ReadAllBytes(Path.Combine(_dbPath, "state", "a43.sst")), Is.EqualTo(files["state/a43.sst"]), "the first start must finish its extraction");
            Assert.That(File.Exists(MarkerPath), Is.False, "the first start must remove its marker when it finishes");
            Assert.That(server.RequestCount, Is.EqualTo(2), "the second start must not download anything");
        }

        // The lock is free again: a later start takes it and skips the initialized database as before.
        await new InitDatabaseSnapshot(_api, DrivesWithFreeSpace(long.MaxValue)).Execute(timeout.Token);
        Assert.That(server.RequestCount, Is.EqualTo(2), "a later start must skip the initialized database");
    }

    [TestCase(false, TestName = "Execute_LockHeldElsewhere_StopsAtOnceAndKeepsTheUnfinishedExtraction")]
    [TestCase(true, TestName = "Execute_LockHeldElsewhereWithUnwrittenMarker_ReportsTheLock")]
    public async Task Execute_LockHeldElsewhere_StopsAtOnceAndKeepsTheUnfinishedExtraction(bool unwrittenMarker)
    {
        InitDatabaseSnapshot step = new(_api, DrivesWithFreeSpace(long.MaxValue));
        await FailExtraction(step);
        // The holder created its marker and has not written it yet: read before the lock, it would be reported as a foreign file.
        if (unwrittenMarker)
            File.WriteAllBytes(MarkerPath, []);
        File.WriteAllBytes(_snapshotPath, TestArchive.BuildTar(TestArchive.BuildFiles(seed: 43)));
        string[] entries = Directory.GetFileSystemEntries(_dbPath, "*", SearchOption.AllDirectories);
        using FileStream held = HoldLock(); // as a second process starting on the same database would
        Stopwatch elapsed = Stopwatch.StartNew();

        IOException exception = (await Assert.ThrowsAsync<IOException>(() => step.Execute(CancellationToken.None)))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(elapsed.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)), "the start must stop at once, without the restart delay");
            Assert.That(exception.Message, Does.Contain("Another process is initializing the database").And.Contain(LockPath),
                "the error must say why the start stops and name the lock file");
            Assert.That(Directory.GetFileSystemEntries(_dbPath, "*", SearchOption.AllDirectories), Is.EquivalentTo(entries),
                "nothing may be deleted, the marker included");
            Assert.That(ReadCheckpoint(), Is.EqualTo(SnapshotStage.Verified), "the checkpoint must stay");
        }
    }

    [TestCase(false, TestName = "Execute_AfterFailedRun_ReleasesTheLock")]
    [TestCase(true, TestName = "Execute_AfterCancelledRun_ReleasesTheLock")]
    public async Task Execute_AfterFailedOrCancelledRun_ReleasesTheLock(bool cancel)
    {
        InitDatabaseSnapshot step = new(_api, DrivesWithFreeSpace(long.MaxValue));
        await FailExtraction(step);
        if (cancel)
        {
            // Stopped in the restart delay, as Ctrl+C would.
            using CancellationTokenSource stopped = new();
            stopped.Cancel();
            await Assert.CatchAsync<OperationCanceledException>(() => step.Execute(stopped.Token), "precondition: the restart must be cancelled");
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.Exists(LockPath), Is.True, "the step must lock the database path with a file next to it");
            Assert.That(() => HoldLock().Dispose(), Throws.Nothing, "a run that failed or was cancelled must release its lock");
        }
    }

    [Test]
    public async Task Execute_RestartedExtraction_KeepsTheLockFileOutsideTheDatabase()
    {
        InitDatabaseSnapshot step = new(_api, DrivesWithFreeSpace(long.MaxValue));
        await FailExtraction(step);
        File.WriteAllBytes(_snapshotPath, TestArchive.BuildTar(TestArchive.BuildFiles(seed: 43)));

        await step.Execute(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.Exists(LockPath), Is.True, "the cleanup must keep the lock file: it sits next to the database directory, not in it");
            Assert.That(Directory.EnumerateFileSystemEntries(_dbPath).Select(Path.GetFileName), Is.EquivalentTo(new[] { "state", "headers" }),
                "the lock file is no part of the database");
        }
    }

    [Test]
    public async Task Execute_DatabasePathWithTrailingSeparator_KeepsTheLockNextToTheDirectory()
    {
        _api.Config<IInitConfig>().Returns(new InitConfig { BaseDbPath = _dbPath + Path.DirectorySeparatorChar });
        WriteSnapshotTar();
        AdvanceCheckpoint(SnapshotStage.Verified);

        await new InitDatabaseSnapshot(_api, DrivesWithFreeSpace(long.MaxValue)).Execute(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.Exists(LockPath), Is.True, "db/ and db must share the lock file next to the directory");
            Assert.That(File.Exists(Path.Combine(_dbPath, "state.bin")), Is.True, "a lock file inside would make a fresh database look non-empty");
        }
    }

    [TestCase(false, TestName = "Execute_LockPathIsADirectory_WarnsAndInitializes")]
    [TestCase(true, TestName = "Execute_LockFileNameTooLong_WarnsAndInitializes")]
    public async Task Execute_LockFileCannotBeOpened_WarnsAndInitializes(bool nameTooLong)
    {
        if (nameTooLong)
        {
            // With the suffix, the name exceeds the 255-byte limit: an IOException that is not a sharing violation.
            _dbPath = Path.Combine(_tempDir.Path, new string('d', 250));
            _api.Config<IInitConfig>().Returns(new InitConfig { BaseDbPath = _dbPath });
        }
        else
        {
            // Opening a directory as a file fails as a permission error does, with an UnauthorizedAccessException, also as root.
            Directory.CreateDirectory(LockPath);
        }

        WriteSnapshotTar();
        AdvanceCheckpoint(SnapshotStage.Verified);
        TestLogger logger = new();
        _api.LogManager.Returns(new OneLoggerLogManager(new(logger)));

        await new InitDatabaseSnapshot(_api, DrivesWithFreeSpace(long.MaxValue)).Execute(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.Exists(Path.Combine(_dbPath, "state.bin")), Is.True,
                "a lock file that cannot be opened is not another process's lock, and must not stop a start that worked before the lock existed");
            Assert.That(logger.LogList.Any(static line => line.Contains("Could not take the lock file")), Is.True, "the start must say it runs without the lock");
        }
    }

    [TestCase(1_000, 0, 2_500, TestName = "FreshDownload")]
    [TestCase(1_000, 400, 2_100, TestName = "ResumedDownload")]
    public void GetRequiredSpaceForDownload_ForGivenSizes_AddsRemainingBytesToExtractionEstimate(
        long totalSize, long existingSize, long expected) =>
        Assert.That(SnapshotDiskSpace.GetRequiredSpaceForDownload(totalSize, existingSize), Is.EqualTo(expected),
            "the pre-download requirement should be the remaining bytes plus the extraction estimate of the full snapshot");

    [TestCase(99, 1_000, true, TestName = "Check_DatabaseDriveShort_ThrowsIOException")]
    [TestCase(100, 99, false, TestName = "Check_OnlyLinkedDriveShort_DoesNotThrow")]
    public void Check_HoldsOnlyTheDatabaseDriveToTheWholeSize(long databaseDriveFree, long linkedDriveFree, bool throws)
    {
        IDriveInfo[] drives = [.. DrivesWithFreeSpace(databaseDriveFree), .. DrivesWithFreeSpace(linkedDriveFree)];

        Assert.That(() => SnapshotDiskSpace.Check(drives, 100, "extract"),
            throws ? Throws.TypeOf<IOException>() : Throws.Nothing,
            "only the database directory's drive must hold the whole snapshot; a linked disk holds only part of the database");
    }

    private long WriteSnapshotTar()
    {
        File.WriteAllBytes(_snapshotPath, TestArchive.BuildTar(SnapshotPayloadSize));
        return new FileInfo(_snapshotPath).Length;
    }

    // Spelled out rather than taken from the production code: the name is an on-disk format.
    private string MarkerPath => Path.Combine(_dbPath, ".nethermind-snapshot-extracting");

    private string LockPath => _dbPath + ".snapshot.lock";

    private FileStream HoldLock() => new(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    private void AdvanceCheckpoint(SnapshotStage stage) =>
        new SnapshotCheckpoint(_snapshotConfig, LimboLogs.Instance).Advance(stage);

    private SnapshotStage ReadCheckpoint() => new SnapshotCheckpoint(_snapshotConfig, LimboLogs.Instance).Read();

    // An extraction that writes the files of seed 42 and then fails, as one cut short does.
    private async Task FailExtraction(InitDatabaseSnapshot step)
    {
        File.WriteAllBytes(_snapshotPath, TestArchive.BuildTar(TestArchive.BuildFiles(seed: 42), endWithLink: true));
        AdvanceCheckpoint(SnapshotStage.Verified);
        await Assert.ThrowsAsync<IOException>(() => step.Execute(CancellationToken.None),
            "precondition: the extraction must fail after writing the files");
    }

    private static IDriveInfo[] DrivesWithFreeSpace(long freeSpace)
    {
        IDriveInfo drive = Substitute.For<IDriveInfo>();
        drive.AvailableFreeSpace.Returns(freeSpace);
        drive.RootDirectory.FullName.Returns("/");
        return [drive];
    }

    private sealed class SnapshotServer : IDisposable
    {
        private readonly HttpListener _listener;

        public string Url { get; }

        private SnapshotServer(HttpListener listener, string url)
        {
            _listener = listener;
            Url = url;
        }

        public static SnapshotServer Start(long contentLength)
        {
            (HttpListener listener, int port) = TestHttpListener.Start();

            _ = Task.Run(async () =>
            {
                while (listener.IsListening)
                {
                    try
                    {
                        HttpListenerContext context = await listener.GetContextAsync();
                        context.Response.ContentLength64 = contentLength;
                        byte[] chunk = new byte[1024];
                        await context.Response.OutputStream.WriteAsync(chunk);
                        context.Response.OutputStream.Flush();
                    }
                    catch (Exception)
                    {
                        return;
                    }
                }
            });

            return new SnapshotServer(listener, $"http://127.0.0.1:{port}/snapshot.tar");
        }

        public void Dispose()
        {
            _listener.Stop();
            _listener.Close();
        }
    }
}
