// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.IO.Abstractions;
using System.Net;
using System.Runtime.Versioning;
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

    [TestCase(nameof(SnapshotStage.Verified), TestName = "Execute_InterruptedExtraction_StopsAndDeletesNothing")]
    [TestCase(nameof(SnapshotStage.Extracted), TestName = "Execute_ExtractionStoppedBeforeRemovingItsMarker_StopsAndDeletesNothing")]
    [TestCase(nameof(SnapshotStage.Completed), TestName = "Execute_ExtractionCompletedBeforeRemovingItsMarker_StopsAndDeletesNothing")]
    public async Task Execute_UnfinishedExtraction_StopsAndDeletesNothing(string checkpointOnRestart)
    {
        TestLogger logger = new();
        _api.LogManager.Returns(new OneLoggerLogManager(new(logger)));
        InitDatabaseSnapshot step = new(_api, DrivesWithFreeSpace(long.MaxValue));
        await FailExtraction(step);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.Exists(MarkerPath), Is.True, "the extraction must mark the database directory before writing to it");
            Assert.That(File.Exists(Path.Combine(_dbPath, "state", "a42.sst")), Is.True, "precondition: the failed extraction left files behind");
            Assert.That(logger.LogList.Any(static line => line.Contains("delete its contents by hand")), Is.True,
                "the failed run must say what to do, as it leaves the partial extraction in place");
        }

        File.WriteAllBytes(_snapshotPath, TestArchive.BuildTar(TestArchive.BuildFiles(seed: 43)));
        // Extracted, Completed: the node stopped after the checkpoint advanced and before the marker was removed; the
        // two-phase path removes it after Extracted, the streaming one after Completed, and admission is the same for both.
        AdvanceCheckpoint(Enum.Parse<SnapshotStage>(checkpointOnRestart));
        Dictionary<string, byte[]> content = ContentOf(_dbPath);

        IOException exception = (await Assert.ThrowsAsync<IOException>(() => step.Execute(CancellationToken.None)))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception.Message, Does.Contain("did not finish").And.Contain("delete its contents by hand"),
                "the start must say what the directory holds and what to do");
            Assert.That(ContentOf(_dbPath), Is.EqualTo(content), "nothing in the database directory may be deleted or changed, the marker included");
            Assert.That(ReadCheckpoint(), Is.EqualTo(Enum.Parse<SnapshotStage>(checkpointOnRestart)), "the checkpoint must stay");
        }
    }

    [TestCase("other content", TestName = "Execute_FileWithMarkerNameButOtherContent_ThrowsAndKeepsTheDatabase")]
    [TestCase("empty", TestName = "Execute_EmptyFileAtTheMarkerPath_ThrowsAndKeepsTheDatabase")]
    [TestCase("directory", TestName = "Execute_DirectoryAtTheMarkerPath_ThrowsAndKeepsTheDatabase")]
    [TestCase("dangling link", TestName = "Execute_DanglingLinkAtTheMarkerPath_ThrowsAndKeepsTheDatabase")]
    [TestCase("fifo", TestName = "Execute_FifoAtTheMarkerPath_ThrowsWithoutOpeningIt")]
    public async Task Execute_AnythingAtTheMarkerPath_ThrowsAndKeepsTheDatabase(string marker)
    {
        if (marker is "dangling link" or "fifo" && !OperatingSystem.IsLinux())
            Assert.Ignore("Links and FIFOs are made here on Linux only.");
        string synced = Path.Combine(_dbPath, "000001.sst");
        Directory.CreateDirectory(_dbPath);
        File.WriteAllBytes(synced, [1, 2, 3]);
        switch (marker)
        {
            case "directory":
                Directory.CreateDirectory(MarkerPath);
                break;
            case "dangling link":
                File.CreateSymbolicLink(MarkerPath, Path.Combine(_tempDir.Path, "missing"));
                break;
            case "fifo":
                using (Process mkfifo = Process.Start("mkfifo", MarkerPath)!)
                    mkfifo.WaitForExit();
                break;
            default:
                File.WriteAllText(MarkerPath, marker == "empty" ? "" : "not a marker");
                break;
        }

        // Bounded: a FIFO with no writer blocks whoever opens it, and the start must neither open nor read the marker.
        Task run = Task.Run(() => new InitDatabaseSnapshot(_api, DrivesWithFreeSpace(long.MaxValue)).Execute(CancellationToken.None));

        IOException exception = (await Assert.ThrowsAsync<IOException>(() => run.WaitAsync(TimeSpan.FromSeconds(30))))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception.Message, Does.Contain("did not finish"), "anything at the marker path shows an unfinished extraction");
            Assert.That(File.ReadAllBytes(synced), Is.EqualTo(new byte[] { 1, 2, 3 }), "nothing may be deleted");
            Assert.That(Directory.EnumerateFileSystemEntries(_dbPath).Count(), Is.EqualTo(2), "the marker path must stay as it was");
        }
    }

    [Test]
    public async Task Execute_AfterStreamedExtractionWasInterrupted_StopsAndDeletesNothing()
    {
        using FlakySnapshotServer server = new();
        // Uncompressed, so every chunk reaches the extractor as soon as it arrives.
        byte[] archive = TestArchive.BuildTar(TestArchive.BuildFiles(seed: 42));
        server.Content = archive;
        _snapshotConfig.DownloadUrl = server.Url;
        _snapshotConfig.Streaming = true;
        const int chunkSize = 4096;
        // The probe, then one request per chunk. With one connection the last chunk is requested only after all chunks
        // but the last two were consumed, so the first file is extracted by then.
        int lastChunkRequest = 1 + (archive.Length + chunkSize - 1) / chunkSize;
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
            Assert.That(File.Exists(MarkerPath), Is.True, "the stream must mark the database directory before writing to it");
            Assert.That(File.Exists(Path.Combine(_dbPath, "state", "a42.sst")), Is.True, "precondition: the interrupted stream left files behind");
        }

        Dictionary<string, byte[]> content = ContentOf(_dbPath);
        int requestsBefore = server.RequestCount;
        using CancellationTokenSource restartTimeout = new(TimeSpan.FromSeconds(60));

        IOException exception = (await Assert.ThrowsAsync<IOException>(
            () => new InitDatabaseSnapshot(_api, DrivesWithFreeSpace(long.MaxValue)).Execute(restartTimeout.Token)))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception.Message, Does.Contain("delete its contents by hand"), "the start must say what to do");
            Assert.That(ContentOf(_dbPath), Is.EqualTo(content), "nothing the interrupted stream wrote may be deleted or written over");
            Assert.That(server.RequestCount, Is.EqualTo(requestsBefore), "the snapshot must not be streamed again over the partial extraction");
        }
    }

    [Test]
    public async Task Execute_StreamedSnapshotFailsItsChecksum_StopsThisStartAndTheNext()
    {
        using FlakySnapshotServer server = new();
        server.Content = TestArchive.BuildTarZst(TestArchive.BuildFiles());
        _snapshotConfig.SnapshotFileName = "snapshot.tar.zst";
        _snapshotConfig.DownloadUrl = server.Url;
        _snapshotConfig.Streaming = true;
        _snapshotConfig.Checksum = Convert.ToHexString(SHA256.HashData([9, 9, 9]));
        InitDatabaseSnapshot step = new(_api, DrivesWithFreeSpace(long.MaxValue));

        IOException failed = (await Assert.ThrowsAsync<IOException>(() => step.Execute(CancellationToken.None)))!;
        Dictionary<string, byte[]> content = ContentOf(_dbPath);
        int requests = server.RequestCount;
        IOException next = (await Assert.ThrowsAsync<IOException>(() => step.Execute(CancellationToken.None)))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failed.Message, Does.Contain("delete its contents by hand"), "a stream that fails its checksum must stop the start, as the node must not start on it");
            Assert.That(File.Exists(Path.Combine(_dbPath, "state", "a42.sst")), Is.True, "what the stream extracted must stay for the operator");
            Assert.That(File.Exists(MarkerPath), Is.True, "the marker must stay");
            Assert.That(next.Message, Does.Contain("delete its contents by hand"), "the next start must stop too");
            Assert.That(ContentOf(_dbPath), Is.EqualTo(content), "the next start must delete nothing");
            Assert.That(server.RequestCount, Is.EqualTo(requests), "the next start must not stream again");
        }
    }

    [TestCase(false, TestName = "Execute_EmptyLinkedSubdirectoryWithoutMarker_KeepsItAndSkipsTheSnapshot")]
    [TestCase(true, TestName = "Execute_ContentOnlyInLinkedSubdirectoryWithoutMarker_KeepsItAndSkipsTheSnapshot")]
    [Platform(Include = "Linux")] // creating links needs a privilege on Windows
    public async Task Execute_LinkedSubdirectoryWithoutMarker_KeepsItAndSkipsTheSnapshot(bool targetHoldsFiles)
    {
        WriteSnapshotTar();
        AdvanceCheckpoint(SnapshotStage.Verified);
        // Another disk: a link is content, so nothing is written through it and it is not removed.
        string target = Path.Combine(_tempDir.Path, "other", "state");
        Directory.CreateDirectory(target);
        if (targetHoldsFiles)
            File.WriteAllBytes(Path.Combine(target, "000001.sst"), [1, 2, 3]);
        string link = Path.Combine(_dbPath, "state");
        Directory.CreateDirectory(_dbPath);
        Directory.CreateSymbolicLink(link, target);

        await new InitDatabaseSnapshot(_api, DrivesWithFreeSpace(long.MaxValue)).Execute(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(new DirectoryInfo(link).LinkTarget, Is.EqualTo(target), "the link must stay");
            Assert.That(Directory.EnumerateFileSystemEntries(target).Count(), Is.EqualTo(targetHoldsFiles ? 1 : 0), "the linked disk must stay as it was");
            Assert.That(File.Exists(Path.Combine(_dbPath, "state.bin")), Is.False, "nothing may be extracted next to the link");
            Assert.That(File.Exists(_snapshotPath), Is.True, "the snapshot must be skipped");
        }
    }

    [Test]
    [Platform(Include = "Linux")] // creating links needs a privilege on Windows
    public async Task Execute_EmptyDatabaseDirectoryThatIsALink_InitializesItsTarget()
    {
        WriteSnapshotTar();
        AdvanceCheckpoint(SnapshotStage.Verified);
        string target = Path.Combine(_tempDir.Path, "other", "db");
        Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(_dbPath, target);

        await new InitDatabaseSnapshot(_api, DrivesWithFreeSpace(long.MaxValue)).Execute(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(new DirectoryInfo(_dbPath).LinkTarget, Is.EqualTo(target), "the link must stay");
            Assert.That(File.Exists(Path.Combine(target, "state.bin")), Is.True, "an empty linked database directory is initialized through the link");
            Assert.That(File.Exists(Path.Combine(target, ".nethermind-snapshot-extracting")), Is.False, "the marker must be removed after success");
        }
    }

    [TestCase("file in it", TestName = "Execute_LostAndFoundHoldingAFile_KeepsTheDatabase")]
    [TestCase("file", TestName = "Execute_FileNamedLostAndFound_KeepsTheDatabase")]
    [TestCase("link", TestName = "Execute_LinkNamedLostAndFound_KeepsTheDatabase")]
    public async Task Execute_LostAndFoundThatIsNoEmptyDirectory_KeepsTheDatabase(string lostAndFound)
    {
        if (lostAndFound == "link" && !OperatingSystem.IsLinux())
            Assert.Ignore("Links are made here on Linux only.");
        WriteSnapshotTar();
        AdvanceCheckpoint(SnapshotStage.Verified);
        string path = Path.Combine(_dbPath, "lost+found");
        Directory.CreateDirectory(_dbPath);
        switch (lostAndFound)
        {
            case "file in it":
                Directory.CreateDirectory(path);
                File.WriteAllBytes(Path.Combine(path, "#1234"), [1]);
                break;
            case "file":
                File.WriteAllBytes(path, [1]);
                break;
            default:
                Directory.CreateSymbolicLink(path, Directory.CreateDirectory(Path.Combine(_tempDir.Path, "other")).FullName);
                break;
        }

        Dictionary<string, byte[]> content = ContentOf(_dbPath);

        await new InitDatabaseSnapshot(_api, DrivesWithFreeSpace(long.MaxValue)).Execute(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ContentOf(_dbPath), Is.EqualTo(content), "only an empty lost+found directory counts as no content");
            Assert.That(File.Exists(_snapshotPath), Is.True, "the snapshot must be skipped");
        }
    }

    [TestCase(false, TestName = "Execute_TarEntryInLostAndFound_StopsWithoutWritingIntoIt")]
    [TestCase(true, TestName = "Execute_ZipEntryInLostAndFound_StopsWithoutWritingIntoIt")]
    public async Task Execute_ArchiveEntryInLostAndFound_StopsWithoutWritingIntoIt(bool zip)
    {
        // A freshly mounted disk: its lost+found is empty, so the database directory counts as empty.
        string lostAndFound = Path.Combine(_dbPath, "lost+found");
        Directory.CreateDirectory(lostAndFound);
        Dictionary<string, byte[]> files = new() { ["state/a.sst"] = [1], ["lost+found/#1234"] = [2] };
        if (zip)
        {
            _snapshotConfig.SnapshotFileName = "snapshot.zip";
            _snapshotPath = Path.Combine(_snapshotConfig.SnapshotDirectory, _snapshotConfig.SnapshotFileName);
        }

        File.WriteAllBytes(_snapshotPath, zip ? TestArchive.BuildZip(files) : TestArchive.BuildTar(files));
        AdvanceCheckpoint(SnapshotStage.Verified);

        IOException exception = (await Assert.ThrowsAsync<IOException>(
            () => new InitDatabaseSnapshot(_api, DrivesWithFreeSpace(long.MaxValue)).Execute(CancellationToken.None)))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception.Message, Does.Contain("lost+found"), "the error must name what the archive would write into");
            Assert.That(Directory.EnumerateFileSystemEntries(lostAndFound), Is.Empty, "nothing may be written into lost+found");
            Assert.That(File.Exists(MarkerPath), Is.True, "the extraction that stopped must stay marked for the operator");
        }
    }

    [Test]
    public async Task Execute_ArchiveWithAnEmptyLostAndFound_ExtractsAroundIt()
    {
        string lostAndFound = Path.Combine(_dbPath, "lost+found");
        Directory.CreateDirectory(lostAndFound);
        // The archive of a database at a mount root holds the directory entry of its lost+found, which writes nothing into it.
        File.WriteAllBytes(_snapshotPath, TestArchive.BuildTar(TestArchive.BuildFiles(), emptyDirectory: "lost+found"));
        AdvanceCheckpoint(SnapshotStage.Verified);

        await new InitDatabaseSnapshot(_api, DrivesWithFreeSpace(long.MaxValue)).Execute(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.Exists(Path.Combine(_dbPath, "state", "a42.sst")), Is.True, "the snapshot must be extracted");
            Assert.That(Directory.EnumerateFileSystemEntries(lostAndFound), Is.Empty, "lost+found must stay empty");
        }
    }

    [Test]
    [Platform(Include = "Linux")] // Unix permissions
    [SupportedOSPlatform("linux")]
    public async Task Execute_LostAndFoundThatCannotBeListed_KeepsTheDatabase()
    {
        // Needs a user the permissions apply to: as root every directory stays readable, and the test is skipped.
        WriteSnapshotTar();
        AdvanceCheckpoint(SnapshotStage.Verified);
        string lostAndFound = Path.Combine(_dbPath, "lost+found");
        Directory.CreateDirectory(lostAndFound);
        UnixFileMode mode = File.GetUnixFileMode(lostAndFound);
        File.SetUnixFileMode(lostAndFound, UnixFileMode.None);
        try
        {
            if (CanList(lostAndFound))
                Assert.Ignore("The permissions do not apply to this user (root); run as another user.");

            await new InitDatabaseSnapshot(_api, DrivesWithFreeSpace(long.MaxValue)).Execute(CancellationToken.None);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(File.Exists(Path.Combine(_dbPath, "state.bin")), Is.False, "a lost+found that cannot be listed may hold content");
                Assert.That(File.Exists(_snapshotPath), Is.True, "the snapshot must be skipped");
            }
        }
        finally
        {
            File.SetUnixFileMode(lostAndFound, mode);
        }
    }

    [TestCase("", false, TestName = "Execute_SnapshotDirectoryIsTheDatabaseDirectory_StopsBeforeAnyRequest")]
    [TestCase("", true, TestName = "Execute_StreamingWithTheDatabaseDirectoryAsSnapshotDirectory_StopsBeforeAnyRequest")]
    [TestCase("snapshot", false, TestName = "Execute_SnapshotDirectoryInsideTheDatabaseDirectory_StopsBeforeAnyRequest")]
    [TestCase("other/../snapshot/", true, TestName = "Execute_StreamingWithASnapshotDirectoryInsideTheDatabaseDirectory_StopsBeforeAnyRequest")]
    public async Task Execute_SnapshotDirectoryInTheDatabase_StopsBeforeAnyRequest(string inside, bool streaming)
    {
        using FlakySnapshotServer server = StreamingServer();
        _snapshotConfig.Streaming = streaming;
        _snapshotConfig.SnapshotDirectory = Path.Combine(_dbPath, inside);
        // Bounded: a start that went on would keep retrying its download into a snapshot directory it did not create.
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));

        InvalidOperationException exception = (await Assert.ThrowsAsync<InvalidOperationException>(
            () => new InitDatabaseSnapshot(_api, DrivesWithFreeSpace(long.MaxValue)).Execute(timeout.Token)))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception.Message, Does.Contain("Snapshot.SnapshotDirectory"), "the start must name the setting to change");
            Assert.That(server.RequestCount, Is.Zero, "nothing may be downloaded");
            Assert.That(Directory.Exists(_dbPath), Is.False, "nothing may be created in the database directory, the snapshot directory included");
        }
    }

    [TestCase("sub/snapshot.tar.zst", TestName = "Execute_SnapshotFileNameWithADirectory_StopsBeforeAnyRequest")]
    [TestCase("../snapshot.tar.zst", TestName = "Execute_SnapshotFileNameOutsideTheSnapshotDirectory_StopsBeforeAnyRequest")]
    [TestCase("in the database", TestName = "Execute_RootedSnapshotFileName_StopsBeforeAnyRequest")]
    [TestCase("..", TestName = "Execute_SnapshotFileNameOfTheParentDirectory_StopsBeforeAnyRequest")]
    public async Task Execute_SnapshotFileNameThatIsNoPlainName_StopsBeforeAnyRequest(string name)
    {
        using FlakySnapshotServer server = StreamingServer();
        _snapshotConfig.Streaming = false;
        _snapshotConfig.SnapshotFileName = name == "in the database" ? Path.Combine(_dbPath, "snapshot.tar.zst") : name;
        string[] staging = Directory.GetFileSystemEntries(_snapshotConfig.SnapshotDirectory);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));

        InvalidOperationException exception = (await Assert.ThrowsAsync<InvalidOperationException>(
            () => new InitDatabaseSnapshot(_api, DrivesWithFreeSpace(long.MaxValue)).Execute(timeout.Token)))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception.Message, Does.Contain("Snapshot.SnapshotFileName"), "the start must name the setting to change");
            Assert.That(server.RequestCount, Is.Zero, "nothing may be downloaded");
            Assert.That(Directory.Exists(_dbPath), Is.False, "nothing may be written into the database directory");
            Assert.That(Directory.GetFileSystemEntries(_snapshotConfig.SnapshotDirectory), Is.EquivalentTo(staging), "nothing may be written into the snapshot directory");
        }
    }

    [TestCase(false, TestName = "Execute_SnapshotDirectoryBesideTheDatabaseWithTheSameStart_Initializes")]
    [TestCase(true, TestName = "Execute_DatabaseThatHoldsContentAndTheSnapshotDirectorySetting_KeepsItAsBefore")]
    public async Task Execute_SnapshotDirectoryThatNeedsNoCheck_GoesOnAsBefore(bool populated)
    {
        using FlakySnapshotServer server = StreamingServer();
        // Beside: whole path components count, so db-snapshot is not inside db.
        _snapshotConfig.SnapshotDirectory = populated ? Path.Combine(_dbPath, "snapshot") : _dbPath + "-snapshot";
        string synced = Path.Combine(_dbPath, "state", "000001.sst");
        if (populated)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(synced)!);
            File.WriteAllBytes(synced, [1, 2, 3]);
        }

        await new InitDatabaseSnapshot(_api, DrivesWithFreeSpace(long.MaxValue)).Execute(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            if (populated)
            {
                Assert.That(File.Exists(synced), Is.True, "a database that holds data is kept, whatever the setting");
                Assert.That(server.RequestCount, Is.Zero, "the snapshot must be skipped");
                Assert.That(Directory.Exists(_snapshotConfig.SnapshotDirectory), Is.False, "nothing may be created in the database directory");
            }
            else
            {
                Assert.That(File.Exists(Path.Combine(_dbPath, "state", "a42.sst")), Is.True, "a directory beside the database is a valid setting");
            }
        }
    }

    // Streaming from a server that counts the requests it gets.
    private FlakySnapshotServer StreamingServer()
    {
        FlakySnapshotServer server = new();
        byte[] archive = TestArchive.BuildTarZst(TestArchive.BuildFiles());
        server.Content = archive;
        _snapshotConfig.SnapshotFileName = "snapshot.tar.zst";
        _snapshotConfig.DownloadUrl = server.Url;
        _snapshotConfig.Streaming = true;
        _snapshotConfig.Checksum = Convert.ToHexString(SHA256.HashData(archive));
        return server;
    }

    private static bool CanList(string directory)
    {
        try
        {
            Directory.GetFileSystemEntries(directory);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    [Test]
    public async Task Execute_SecondStartDuringAnExtraction_StopsAtOnceAndLetsTheFirstFinish()
    {
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

        Task second = new InitDatabaseSnapshot(_api, DrivesWithFreeSpace(long.MaxValue)).Execute(timeout.Token);
        Assert.That(File.Exists(MarkerPath), Is.True, "the second start must leave what the first one is extracting alone");
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
            Assert.That(elapsed.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)), "the start must stop at once");
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
        if (cancel)
        {
            // Stopped as the extraction begins, as Ctrl+C would.
            WriteSnapshotTar();
            AdvanceCheckpoint(SnapshotStage.Verified);
            using CancellationTokenSource stopped = new();
            stopped.Cancel();
            await Assert.CatchAsync<OperationCanceledException>(() => step.Execute(stopped.Token), "precondition: the extraction must be cancelled");
        }
        else
        {
            await FailExtraction(step);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.Exists(LockPath), Is.True, "the step must lock the database path with a file next to it");
            Assert.That(() => HoldLock().Dispose(), Throws.Nothing, "a run that failed or was cancelled must release its lock");
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

    // Every entry below the directory with its bytes (none for a directory), to show that nothing was deleted or changed.
    private static Dictionary<string, byte[]> ContentOf(string directory) =>
        Directory.GetFileSystemEntries(directory, "*", SearchOption.AllDirectories).ToDictionary(
            entry => Path.GetRelativePath(directory, entry), entry => Directory.Exists(entry) ? Array.Empty<byte>() : File.ReadAllBytes(entry));

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
