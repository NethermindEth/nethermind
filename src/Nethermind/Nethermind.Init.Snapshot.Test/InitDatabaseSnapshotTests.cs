// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.IO.Abstractions;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
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
    private const int LongPathLength = 4080;

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

    [TestCase(null, false, TestName = "Execute_PopulatedWithoutMarkerOrCheckpoint_KeepsItAndWarns")]
    [TestCase(null, true, TestName = "Execute_PopulatedWithoutMarkerOrCheckpointWhenStreaming_KeepsItAndWarns")]
    [TestCase("Started", false, TestName = "Execute_PopulatedWithoutMarkerAtStarted_KeepsItAndWarns")]
    [TestCase("Downloaded", true, TestName = "Execute_PopulatedWithoutMarkerAtDownloaded_KeepsItAndWarns")]
    [TestCase("Verified", false, TestName = "Execute_PopulatedWithoutMarkerAtVerified_KeepsItAndWarns")]
    [TestCase("NotAStage", true, TestName = "Execute_PopulatedWithoutMarkerAndAnUnknownCheckpoint_KeepsItAndWarns")]
    [TestCase("Extracted", false, TestName = "Execute_PopulatedWithoutMarkerAtExtracted_SkipsAsBefore")]
    [TestCase("Completed", true, TestName = "Execute_PopulatedWithoutMarkerAtCompleted_SkipsAsBefore")]
    public async Task Execute_NonEmptyDatabaseWithoutMarker_KeepsItAndSkipsTheSnapshot(string? checkpoint, bool streaming)
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
        InterfaceLogger logger = Substitute.For<InterfaceLogger>();
        logger.IsInfo.Returns(true);
        logger.IsWarn.Returns(true);
        // Made first: the logger reads the levels of the substitute, which must not happen inside Returns.
        OneLoggerLogManager logManager = new(new(logger));
        _api.LogManager.Returns(logManager);
        bool warns = checkpoint is not ("Extracted" or "Completed");

        await new InitDatabaseSnapshot(_api, DrivesWithFreeSpace(long.MaxValue)).Execute(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.ReadAllBytes(synced), Is.EqualTo(new byte[] { 1, 2, 3 }), "a database without the extraction marker is not the snapshot's to change");
            Assert.That(server.RequestCount, Is.Zero, "the snapshot must be skipped, not downloaded over the kept database");
        }

        // A kept database is a warning with the way to get the snapshot; one the snapshot completed is skipped as before.
        logger.Received(warns ? 1 : 0).Warn(Arg.Is<string>(line => line.Contains("holds content and no snapshot extraction marker")));
        logger.Received(warns ? 0 : 1).Info(Arg.Is<string>(line => line.Contains("Database already exists")));
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
            Assert.That(logger.LogList.Any(static line => line.Contains("Snapshot extraction did not finish") && line.Contains("empty the directory by hand")), Is.True,
                "the failed run must say what to do, as it leaves the partial extraction in place");
        }

        File.WriteAllBytes(_snapshotPath, TestArchive.BuildTar(TestArchive.BuildFiles(seed: 43)));
        // Simulate a marker left with a legacy or inconsistent success checkpoint. Marker presence still stops admission.
        AdvanceCheckpoint(Enum.Parse<SnapshotStage>(checkpointOnRestart));
        Dictionary<string, byte[]> content = ContentOf(_dbPath);

        IOException exception = (await Assert.ThrowsAsync<IOException>(() => step.Execute(CancellationToken.None)))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception.Message, Does.Contain("was not completed").And.Contain("empty the directory by hand"),
                "the start must say what the directory holds and what to do");
            Assert.That(ContentOf(_dbPath), Is.EqualTo(content), "nothing in the database directory may be deleted or changed, the marker included");
            Assert.That(ReadCheckpoint(), Is.EqualTo(Enum.Parse<SnapshotStage>(checkpointOnRestart)), "the checkpoint must stay");
        }

        // What the guidance says: the operator empties the directory, the hidden marker included, and the next start
        // initializes from the archive.
        Directory.Delete(_dbPath, recursive: true);
        Directory.CreateDirectory(_dbPath);

        await step.Execute(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.Exists(Path.Combine(_dbPath, "state", "a43.sst")), Is.True, "the emptied directory must be initialized");
            Assert.That(File.Exists(MarkerPath), Is.False, "the marker must be removed after success");
            Assert.That(ReadCheckpoint(), Is.EqualTo(SnapshotStage.Completed), "the checkpoint must complete");
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
            Assert.That(exception.Message, Does.Contain("was not completed"), "anything at the marker path shows an unfinished extraction");
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
            Assert.That(exception.Message, Does.Contain("empty the directory by hand"), "the start must say what to do");
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
            Assert.That(failed.Message, Does.Contain("did not pass its checksum verification").And.Contain("empty the directory by hand"),
                "a stream that fails its checksum must stop the start, as the node must not start on it");
            Assert.That(File.Exists(Path.Combine(_dbPath, "state", "a42.sst")), Is.True, "what the stream extracted must stay for the operator");
            Assert.That(File.Exists(MarkerPath), Is.True, "the marker must stay");
            Assert.That(ReadCheckpoint(), Is.EqualTo(SnapshotStage.Started), "the checkpoint must not advance when the checksum fails");
            Assert.That(next.Message, Does.Contain("empty the directory by hand"), "the next start must stop too");
            Assert.That(ContentOf(_dbPath), Is.EqualTo(content), "the next start must delete nothing");
            Assert.That(server.RequestCount, Is.EqualTo(requests), "the next start must not stream again");
        }
    }

    [TestCase(false, TestName = "Execute_MarkerAppearsDuringTheDownload_TheClaimReportsTheOtherExtraction")]
    [TestCase(true, TestName = "Execute_MarkerAndDataAppearDuringTheDownload_TheClaimReportsTheOtherExtraction")]
    public async Task Execute_ExtractionAppearsDuringTheDownload_TheClaimReportsIt(bool withData)
    {
        using FlakySnapshotServer server = StreamingServer();
        _snapshotConfig.Streaming = false;
        _snapshotPath = Path.Combine(_snapshotConfig.SnapshotDirectory, _snapshotConfig.SnapshotFileName);
        string data = Path.Combine(_dbPath, "state", "000001.sst");
        // The probe, then the download: while it runs, an extraction the lock does not keep out marks the database directory.
        int requests = 0;
        server.BeforeResponse = _ =>
        {
            if (Interlocked.Increment(ref requests) == 2)
            {
                Directory.CreateDirectory(_dbPath);
                File.WriteAllBytes(MarkerPath, [4, 5, 6]);
                if (withData)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(data)!);
                    File.WriteAllBytes(data, [1, 2, 3]);
                }
            }

            return Task.CompletedTask;
        };

        IOException exception = (await Assert.ThrowsAsync<IOException>(
            () => new InitDatabaseSnapshot(_api, DrivesWithFreeSpace(long.MaxValue)).Execute(CancellationToken.None)))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception.Message, Does.Contain("was not completed, or is still running in another process").And.Contain("empty the directory by hand"),
                "the claim must say that another extraction marked the directory");
            Assert.That(File.ReadAllBytes(MarkerPath), Is.EqualTo(new byte[] { 4, 5, 6 }), "the other extraction's marker must stay as it was");
            if (withData)
                Assert.That(File.ReadAllBytes(data), Is.EqualTo(new byte[] { 1, 2, 3 }), "the other extraction's file must stay as it was");
            Assert.That(Directory.GetFileSystemEntries(_dbPath, "*", SearchOption.AllDirectories).Length, Is.EqualTo(withData ? 3 : 1), "nothing may be extracted");
        }
    }

    [TestCase(false, TestName = "Execute_DatabaseDirectoryThatCannotBeSearched_StopsWithoutTakingItForADatabase")]
    [TestCase(true, TestName = "Execute_DatabaseDirectoryThatCannotBeSearchedWithMaskedPaths_StopsAndRedactsTheWholeChain")]
    [Platform(Include = "Linux")] // Unix permissions
    [SupportedOSPlatform("linux")]
    public async Task Execute_DatabaseDirectoryThatCannotBeSearched_StopsWithoutTakingItForADatabase(bool masked)
    {
        // As another user than root the marker cannot be looked up; as root it is found. Either way the start must stop,
        // and only a marker that was found may be reported as one.
        WriteSnapshotTar();
        AdvanceCheckpoint(SnapshotStage.Verified);
        string data = Path.Combine(_dbPath, "000001.sst");
        Directory.CreateDirectory(_dbPath);
        File.WriteAllBytes(MarkerPath, []);
        File.WriteAllBytes(data, [1, 2, 3]);
        UnixFileMode mode = File.GetUnixFileMode(_dbPath);
        File.SetUnixFileMode(_dbPath, UnixFileMode.None);
        bool wasMasked = SensitiveLogMasking.Enabled;
        SensitiveLogMasking.Enabled = masked;
        bool denied = !CanSearch(_dbPath);
        IOException exception;
        try
        {
            exception = (await Assert.ThrowsAsync<IOException>(
                () => new InitDatabaseSnapshot(_api, DrivesWithFreeSpace(long.MaxValue)).Execute(CancellationToken.None)))!;
        }
        finally
        {
            SensitiveLogMasking.Enabled = wasMasked;
            File.SetUnixFileMode(_dbPath, mode);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception.Message, Does.Contain(".nethermind-snapshot-extracting"), "the error must name what could not be checked or was found");
            Assert.That(exception.Message, denied
                ? Does.Contain("Could not check").And.Not.Contain("was not completed")
                : Does.Contain("was not completed").And.Not.Contain("Could not check"),
                "a marker that could not be looked up must not be reported as found, and one that was found must be");
            if (masked)
            {
                Assert.That(exception.InnerException, Is.Null, "the cause names the path, so it is dropped when paths are masked");
                for (Exception? link = exception; link is not null; link = link.InnerException)
                    Assert.That(link.Message, Does.Not.Contain(_dbPath), "no message in the chain may name the path");
            }
            else
            {
                Assert.That(exception.Message, Does.Contain(_dbPath), "the error must name the directory");
            }

            Assert.That(File.ReadAllBytes(data), Is.EqualTo(new byte[] { 1, 2, 3 }), "nothing may be changed");
            Assert.That(File.Exists(MarkerPath), Is.True, "the marker must stay");
            Assert.That(File.Exists(_snapshotPath), Is.True, "nothing may be extracted");
        }
    }

    [Test]
    [Platform(Include = "Linux")] // the 4,096-byte path limit
    public async Task Execute_FileAtALongDatabasePath_KeepsItWithoutLookingBelowIt()
    {
        string dbPath = LongDatabasePath();
        File.WriteAllBytes(dbPath, [1, 2, 3]);
        _api.Config<IInitConfig>().Returns(new InitConfig { BaseDbPath = dbPath });
        WriteSnapshotTar();
        AdvanceCheckpoint(SnapshotStage.Verified);

        await new InitDatabaseSnapshot(_api, DrivesWithFreeSpace(long.MaxValue)).Execute(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.ReadAllBytes(dbPath), Is.EqualTo(new byte[] { 1, 2, 3 }), "a regular file at the database path counts as content: it is kept and the snapshot skipped");
            Assert.That(File.Exists(_snapshotPath), Is.True, "the snapshot must be skipped");
        }
    }

    [Test]
    [Platform(Include = "Linux")] // the 4,096-byte path limit
    public async Task Execute_LinkToAFileAtALongDatabasePath_StopsWhenTheMarkerCannotBeLookedUp()
    {
        // A link is looked up below, as a linked database directory is; here the marker's path exceeds the limit, so the
        // lookup fails and the start stops instead of taking the link for content.
        string target = Path.Combine(_tempDir.Path, "target");
        File.WriteAllBytes(target, [1, 2, 3]);
        string dbPath = LongDatabasePath();
        File.CreateSymbolicLink(dbPath, target);
        _api.Config<IInitConfig>().Returns(new InitConfig { BaseDbPath = dbPath });
        WriteSnapshotTar();
        AdvanceCheckpoint(SnapshotStage.Verified);
        byte[] archive = File.ReadAllBytes(_snapshotPath);

        IOException exception = (await Assert.ThrowsAsync<IOException>(
            () => new InitDatabaseSnapshot(_api, DrivesWithFreeSpace(long.MaxValue)).Execute(CancellationToken.None)))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception.Message, Does.Contain("Could not check"), "the start must stop with the inspection error");
            Assert.That(new FileInfo(dbPath).LinkTarget, Is.EqualTo(target), "the link must stay");
            Assert.That(File.ReadAllBytes(target), Is.EqualTo(new byte[] { 1, 2, 3 }), "the link's target must stay as it was");
            Assert.That(File.ReadAllBytes(_snapshotPath), Is.EqualTo(archive), "the staged archive must stay");
        }
    }

    // Build a 4,080-byte UTF-8 path using 200-byte ASCII parent components and a final name of at most 220 bytes. The
    // sibling lock path fits and the marker path exceeds the Linux limit.
    private string LongDatabasePath()
    {
        string parent = _tempDir.Path;
        while (LongPathLength - Encoding.UTF8.GetByteCount(parent) - 1 > 220)
            parent = Path.Combine(parent, new string('p', 200));
        string dbPath = Path.Combine(parent, new string('d', LongPathLength - Encoding.UTF8.GetByteCount(parent) - 1));
        Directory.CreateDirectory(parent);
        Assert.That(Encoding.UTF8.GetByteCount(dbPath), Is.EqualTo(LongPathLength), "precondition: the path has its length in bytes");
        return dbPath;
    }

    [Test]
    public async Task Execute_FileAtTheDatabasePath_KeepsItAndSkipsTheSnapshot()
    {
        WriteSnapshotTar();
        AdvanceCheckpoint(SnapshotStage.Verified);
        File.WriteAllBytes(_dbPath, [1, 2, 3]);

        await new InitDatabaseSnapshot(_api, DrivesWithFreeSpace(long.MaxValue)).Execute(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.ReadAllBytes(_dbPath), Is.EqualTo(new byte[] { 1, 2, 3 }), "a regular file at the database path counts as content: it is kept and the snapshot skipped");
            Assert.That(File.Exists(_snapshotPath), Is.True, "the snapshot must be skipped");
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

    [TestCase("file in it", TestName = "Execute_LostAndFoundHoldingAFile_InitializesAroundIt")]
    [TestCase("file", TestName = "Execute_FileNamedLostAndFound_InitializesAroundIt")]
    [TestCase("link", TestName = "Execute_LinkNamedLostAndFound_InitializesAroundIt")]
    public async Task Execute_LostAndFoundOfAnyKind_InitializesAroundIt(string lostAndFound)
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
            Assert.That(File.Exists(Path.Combine(_dbPath, "state.bin")), Is.True, "lost+found is skipped by name, whatever it is, as on master");
            Assert.That(ContentOf(_dbPath).Where(entry => content.ContainsKey(entry.Key)).ToDictionary(), Is.EqualTo(content), "lost+found must stay as it was");
        }
    }

    [TestCase(false, TestName = "Execute_TarEntryInLostAndFound_StopsWithoutWritingIntoIt")]
    [TestCase(true, TestName = "Execute_ZipEntryInLostAndFound_StopsWithoutWritingIntoIt")]
    public async Task Execute_ArchiveEntryInLostAndFound_StopsWithoutWritingIntoIt(bool zip)
    {
        // What a filesystem check saved: the database directory still counts as empty, and the file must keep its bytes.
        // The archive aims at another, absent file there, so only the refusal can keep it out.
        string recovered = Path.Combine(_dbPath, "lost+found", "#1234");
        Directory.CreateDirectory(Path.GetDirectoryName(recovered)!);
        File.WriteAllBytes(recovered, [7, 7, 7]);
        Dictionary<string, byte[]> files = new() { ["state/a.sst"] = [1], ["lost+found/#5678"] = [2] };
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
            Assert.That(File.ReadAllBytes(recovered), Is.EqualTo(new byte[] { 7, 7, 7 }), "a recovered file must keep its bytes");
            Assert.That(Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(recovered)!).Count(), Is.EqualTo(1), "nothing may be written into lost+found");
            Assert.That(File.Exists(Path.Combine(_dbPath, "state", "a.sst")), Is.EqualTo(!zip),
                "a tar entry is refused when the extraction reaches it; a ZIP archive is checked whole before its first entry is written");
            Assert.That(File.Exists(MarkerPath), Is.True, "the extraction that stopped must stay marked for the operator");
        }
    }

    [TestCase("lost+found", TestName = "Execute_TarFileEntryNamedLostAndFound_StopsWithoutCreatingIt")]
    [TestCase(".nethermind-snapshot-extracting", TestName = "Execute_TarEntryAtTheMarkerPath_StopsAndKeepsTheMarker")]
    public async Task Execute_TarEntryAtAReservedPath_StopsWithoutWritingThere(string name)
    {
        File.WriteAllBytes(_snapshotPath, TestArchive.BuildTar(new Dictionary<string, byte[]> { ["state/a.sst"] = [1], [name] = [2] }));
        AdvanceCheckpoint(SnapshotStage.Verified);

        IOException exception = (await Assert.ThrowsAsync<IOException>(
            () => new InitDatabaseSnapshot(_api, DrivesWithFreeSpace(long.MaxValue)).Execute(CancellationToken.None)))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception.Message, Does.Contain("would extract into lost+found"), "the archive entry must be refused");
            Assert.That(File.Exists(Path.Combine(_dbPath, "state", "a.sst")), Is.True, "precondition: the extraction wrote files before the entry");
            Assert.That(Path.Exists(Path.Combine(_dbPath, "lost+found")), Is.False, "no file may be created at lost+found");
            Assert.That(File.ReadAllText(MarkerPath), Is.EqualTo("Nethermind snapshot extraction in progress"),
                "the marker must stay as the extraction wrote it, or a later start would take the partial extraction for a database");
        }
    }

    [TestCase(false, "directory", TestName = "Execute_TarWithTheDirectoryEntryOfLostAndFound_ExtractsAroundAnEmptyOne")]
    [TestCase(false, "absent", TestName = "Execute_TarWithTheDirectoryEntryOfLostAndFound_DoesNotCreateIt")]
    [TestCase(false, "file", TestName = "Execute_TarWithTheDirectoryEntryOfLostAndFound_ExtractsAroundAFileByThatName")]
    [TestCase(true, "directory", TestName = "Execute_ZipWithTheDirectoryEntryOfLostAndFound_ExtractsAroundAnEmptyOne")]
    [TestCase(true, "absent", TestName = "Execute_ZipWithTheDirectoryEntryOfLostAndFound_DoesNotCreateIt")]
    [TestCase(true, "file", TestName = "Execute_ZipWithTheDirectoryEntryOfLostAndFound_ExtractsAroundAFileByThatName")]
    public async Task Execute_ArchiveWithTheDirectoryEntryOfLostAndFound_SkipsIt(bool zip, string onDisk)
    {
        // The archive of a database at a mount root holds the directory entry of its lost+found, which the extraction skips.
        string lostAndFound = Path.Combine(_dbPath, "lost+found");
        Directory.CreateDirectory(_dbPath);
        if (onDisk == "directory")
            Directory.CreateDirectory(lostAndFound);
        else if (onDisk == "file")
            File.WriteAllBytes(lostAndFound, [7, 7, 7]);
        UseArchiveWithTheDirectoryEntryOfLostAndFound(zip, TestArchive.BuildFiles());

        await new InitDatabaseSnapshot(_api, DrivesWithFreeSpace(long.MaxValue)).Execute(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.ReadAllBytes(Path.Combine(_dbPath, "state", "a42.sst")), Is.EqualTo(TestArchive.BuildFiles()["state/a42.sst"]), "the snapshot must be extracted");
            Assert.That(File.Exists(MarkerPath), Is.False, "the marker must be removed after success");
            switch (onDisk)
            {
                case "directory":
                    Assert.That(Directory.EnumerateFileSystemEntries(lostAndFound), Is.Empty, "lost+found must stay empty");
                    break;
                case "absent":
                    Assert.That(Path.Exists(lostAndFound), Is.False, "the extraction must not create lost+found");
                    break;
                default:
                    Assert.That(File.ReadAllBytes(lostAndFound), Is.EqualTo(new byte[] { 7, 7, 7 }), "a file named lost+found must stay as it was");
                    break;
            }
        }
    }

    [TestCase(false, TestName = "Execute_TarWithOnlyTheDirectoryEntryOfLostAndFound_FailsTheEmptyOutputCheck")]
    [TestCase(true, TestName = "Execute_ZipWithOnlyTheDirectoryEntryOfLostAndFound_FailsTheEmptyOutputCheck")]
    public async Task Execute_ArchiveWithOnlyTheDirectoryEntryOfLostAndFound_FailsTheEmptyOutputCheck(bool zip)
    {
        UseArchiveWithTheDirectoryEntryOfLostAndFound(zip, []);

        InvalidOperationException exception = (await Assert.ThrowsAsync<InvalidOperationException>(
            () => new InitDatabaseSnapshot(_api, DrivesWithFreeSpace(long.MaxValue)).Execute(CancellationToken.None)))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception.Message, Does.Contain("produced no files"), "an archive with nothing but the recovery directory is empty");
            Assert.That(Path.Exists(Path.Combine(_dbPath, "lost+found")), Is.False, "the extraction must not create lost+found");
            Assert.That(File.Exists(MarkerPath), Is.True, "the extraction that stopped must stay marked for the operator");
        }
    }

    [TestCase("state", false, TestName = "Execute_ZipDirectoryEntryThatHoldsData_StopsBeforeWritingAnything")]
    [TestCase("lost+found", false, TestName = "Execute_ZipLostAndFoundEntryThatHoldsData_StopsBeforeWritingAnything")]
    [TestCase("state", true, TestName = "Execute_ZipDirectoryEntryThatHoldsDataAfterAGoodFile_StopsBeforeWritingAnything")]
    public async Task Execute_ZipDirectoryEntryThatHoldsData_StopsBeforeWritingAnything(string directory, bool directoryLast)
    {
        // Alone, before a good file, or after one: the runtime refuses such an entry, and an extraction that skipped or
        // created it would drop its bytes and could complete. After a good file, only a check of every entry before the
        // first is written keeps that file out.
        Dictionary<string, byte[]> files = directory == "state" && !directoryLast ? [] : new() { [directoryLast ? "headers/b.sst" : "state/a.sst"] = [1] };
        _snapshotConfig.SnapshotFileName = "snapshot.zip";
        _snapshotPath = Path.Combine(_snapshotConfig.SnapshotDirectory, _snapshotConfig.SnapshotFileName);
        byte[] archive = TestArchive.BuildZipWithDataInADirectoryEntry(directory, files, directoryLast);
        File.WriteAllBytes(_snapshotPath, archive);
        AdvanceCheckpoint(SnapshotStage.Verified);

        IOException exception = (await Assert.ThrowsAsync<IOException>(
            () => new InitDatabaseSnapshot(_api, DrivesWithFreeSpace(long.MaxValue)).Execute(CancellationToken.None)))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception.Message, Does.Contain("is a directory entry that holds data"), "the entry must be refused");
            Assert.That(Directory.GetFileSystemEntries(_dbPath), Is.EquivalentTo(new[] { MarkerPath }), "nothing may be extracted, and the extraction stays marked");
            Assert.That(File.ReadAllBytes(_snapshotPath), Is.EqualTo(archive), "the archive must stay");
            Assert.That(ReadCheckpoint(), Is.EqualTo(SnapshotStage.Verified), "the checkpoint must not advance");
        }
    }

    [Test]
    public async Task Execute_ZipEntryNamedByAnAbsolutePathOutside_StopsBeforeWritingAnything()
    {
        // On Windows a drive-absolute name, which the runtime's renaming would turn into a relative one.
        string outside = Path.Combine(_tempDir.Path, "outside.bin");
        _snapshotConfig.SnapshotFileName = "snapshot.zip";
        _snapshotPath = Path.Combine(_snapshotConfig.SnapshotDirectory, _snapshotConfig.SnapshotFileName);
        File.WriteAllBytes(_snapshotPath, TestArchive.BuildZip(new Dictionary<string, byte[]> { ["state/a.sst"] = [1], [outside.Replace('\\', '/')] = [2] }));
        AdvanceCheckpoint(SnapshotStage.Verified);

        IOException exception = (await Assert.ThrowsAsync<IOException>(
            () => new InitDatabaseSnapshot(_api, DrivesWithFreeSpace(long.MaxValue)).Execute(CancellationToken.None)))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception.Message, Does.Contain("outside the destination directory"), "the entry must be refused");
            Assert.That(Path.Exists(outside), Is.False, "nothing may be written outside");
            Assert.That(Directory.GetFileSystemEntries(_dbPath), Is.EquivalentTo(new[] { MarkerPath }), "a ZIP archive is checked whole before its first entry is written");
        }
    }

    private void UseArchiveWithTheDirectoryEntryOfLostAndFound(bool zip, Dictionary<string, byte[]> files)
    {
        if (zip)
        {
            _snapshotConfig.SnapshotFileName = "snapshot.zip";
            _snapshotPath = Path.Combine(_snapshotConfig.SnapshotDirectory, _snapshotConfig.SnapshotFileName);
        }

        File.WriteAllBytes(_snapshotPath, zip ? TestArchive.BuildZip(files, emptyDirectory: "lost+found") : TestArchive.BuildTar(files, emptyDirectory: "lost+found"));
        AdvanceCheckpoint(SnapshotStage.Verified);
    }

    [Test]
    [Platform(Include = "Linux")] // Unix permissions
    [SupportedOSPlatform("linux")]
    public async Task Execute_LostAndFoundThatCannotBeListed_Initializes()
    {
        // A fresh ext4 volume as a node that does not run as root sees it: lost+found is root's, mode 0700. As root every
        // directory stays readable, so the case is a real one only as another user.
        WriteSnapshotTar();
        AdvanceCheckpoint(SnapshotStage.Verified);
        string lostAndFound = Path.Combine(_dbPath, "lost+found");
        Directory.CreateDirectory(lostAndFound);
        UnixFileMode mode = File.GetUnixFileMode(lostAndFound);
        File.SetUnixFileMode(lostAndFound, UnixFileMode.None);
        try
        {
            await new InitDatabaseSnapshot(_api, DrivesWithFreeSpace(long.MaxValue)).Execute(CancellationToken.None);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(File.Exists(Path.Combine(_dbPath, "state.bin")), Is.True, "lost+found is skipped by name and never listed");
                Assert.That(File.Exists(MarkerPath), Is.False, "the marker must be removed after success");
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

    [TestCase(false, TestName = "Execute_CheckpointWriteFailsAfterTheExtraction_StopsAndTheNextStartKeepsTheDatabase")]
    [TestCase(true, TestName = "Execute_CheckpointWriteFailsAfterTheStreamedExtraction_StopsAndTheNextStartKeepsTheDatabase")]
    public async Task Execute_CheckpointWriteFailsAfterTheExtraction_StopsAndTheNextStartKeepsTheDatabase(bool streaming)
    {
        using FlakySnapshotServer server = StreamingServer();
        _snapshotConfig.Streaming = streaming;
        if (!streaming)
        {
            _snapshotPath = Path.Combine(_snapshotConfig.SnapshotDirectory, _snapshotConfig.SnapshotFileName);
            File.WriteAllBytes(_snapshotPath, server.Content);
            AdvanceCheckpoint(SnapshotStage.Verified);
        }

        SnapshotStage before = ReadCheckpoint();
        // A directory where the checkpoint writes its temporary file: the success checkpoint cannot be written.
        Directory.CreateDirectory(Path.Combine(_snapshotConfig.SnapshotDirectory, $"checkpoint_{_snapshotConfig.SnapshotFileName}.tmp"));
        TestLogger logger = new();
        _api.LogManager.Returns(new OneLoggerLogManager(new(logger)));
        InitDatabaseSnapshot step = new(_api, DrivesWithFreeSpace(long.MaxValue));

        Exception? failed = await Assert.CatchAsync(() => step.Execute(CancellationToken.None));
        Dictionary<string, byte[]> content = ContentOf(_dbPath);
        int requests = server.RequestCount;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failed, Is.InstanceOf<IOException>().Or.InstanceOf<UnauthorizedAccessException>(), "the failed write must stop the start");
            Assert.That(failed!.Message, Does.Contain($"checkpoint_{_snapshotConfig.SnapshotFileName}.tmp"), "the error must name the path that failed");
            Assert.That(File.Exists(MarkerPath), Is.False, "the marker goes before the checkpoint once the extraction succeeded");
            Assert.That(File.ReadAllBytes(Path.Combine(_dbPath, "state", "a42.sst")), Is.EqualTo(TestArchive.BuildFiles()["state/a42.sst"]), "the database must be complete");
            Assert.That(ReadCheckpoint(), Is.EqualTo(before), "precondition: the checkpoint did not advance");
            Assert.That(logger.LogList.Any(static line => line.Contains("successfully initialized")), Is.False, "a start that fails must not report success");
            if (!streaming)
                Assert.That(File.ReadAllBytes(_snapshotPath), Is.EqualTo(server.Content), "the archive must stay until the extraction is recorded");
        }

        await step.Execute(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ContentOf(_dbPath), Is.EqualTo(content), "the next start must keep the complete database as it is");
            Assert.That(server.RequestCount, Is.EqualTo(requests), "the next start must not download again");
        }
    }

    [Test]
    public async Task Execute_SnapshotDirectoryInTheDatabaseByLetterCase_StopsWhereTheCaseIsIgnored()
    {
        using FlakySnapshotServer server = StreamingServer();
        // The database directory spelled in other letter case: the same directory on Windows and macOS, another one on Linux.
        _snapshotConfig.SnapshotDirectory = Path.Combine(Path.GetDirectoryName(_dbPath)!, "DB", "snapshot");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        Task run = new InitDatabaseSnapshot(_api, DrivesWithFreeSpace(long.MaxValue)).Execute(timeout.Token);

        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            InvalidOperationException exception = (await Assert.ThrowsAsync<InvalidOperationException>(() => run))!;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(exception.Message, Does.Contain("Snapshot.SnapshotDirectory"), "the start must name the setting to change");
                Assert.That(server.RequestCount, Is.Zero, "nothing may be downloaded");
            }
        }
        else
        {
            await run;
            Assert.That(File.Exists(Path.Combine(_dbPath, "state", "a42.sst")), Is.True, "on Linux letter case tells two directories apart");
        }
    }

    // Whether this user may look up names in the directory: as root, permissions do not apply.
    private static bool CanSearch(string directory)
    {
        try
        {
            _ = File.GetAttributes(Path.Combine(directory, "probe"));
            return true;
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
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

    [TestCase(true, TestName = "Execute_SecondStartDuringAStreamedExtraction_StopsAtOnceAndLetsTheFirstFinish")]
    [TestCase(false, TestName = "Execute_SecondStartDuringADownloadBeforeAnyMarker_StopsAtOnceAndLetsTheFirstFinish")]
    public async Task Execute_SecondStartDuringAnInitialization_StopsAtOnceAndLetsTheFirstFinish(bool streaming)
    {
        using FlakySnapshotServer server = StreamingServer();
        _snapshotConfig.Streaming = streaming;
        // The probe, then the archive: the first start is held at the archive request. Streaming, it has marked the
        // database by then; two-phase, it is downloading, before any marker, into the archive the second would share.
        TaskCompletionSource held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int requests = 0;
        server.BeforeResponse = async token =>
        {
            if (Interlocked.Increment(ref requests) != 2)
                return;
            held.TrySetResult();
            await resume.Task.WaitAsync(token);
        };
        string checkpointPath = Path.Combine(_snapshotConfig.SnapshotDirectory, $"checkpoint_{_snapshotConfig.SnapshotFileName}");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));
        Task first = new InitDatabaseSnapshot(_api, DrivesWithFreeSpace(long.MaxValue)).Execute(timeout.Token);
        try
        {
            await held.Task.WaitAsync(TimeSpan.FromSeconds(30));
            string[] entries = Directory.GetFileSystemEntries(_tempDir.Path, "*", SearchOption.AllDirectories);
            SnapshotStage checkpoint = ReadCheckpoint();
            Stopwatch elapsed = Stopwatch.StartNew();

            IOException exception = (await Assert.ThrowsAsync<IOException>(
                () => new InitDatabaseSnapshot(_api, DrivesWithFreeSpace(long.MaxValue)).Execute(timeout.Token)))!;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(elapsed.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)), "the second start must stop at once");
                Assert.That(exception.Message, Does.Contain("Another process is initializing the database").And.Contain(LockPath),
                    "the second start must say that the first holds the lock, also while the first one's marker is there, and name the lock file");
                Assert.That(server.RequestCount, Is.EqualTo(2), "the second start must not request anything");
                Assert.That(File.Exists(MarkerPath), Is.EqualTo(streaming), "precondition: the first start is held where expected");
                Assert.That(Directory.GetFileSystemEntries(_tempDir.Path, "*", SearchOption.AllDirectories), Is.EquivalentTo(entries),
                    "the second start must leave the database, the archive, the checkpoint and the marker alone");
                Assert.That(ReadCheckpoint(), Is.EqualTo(checkpoint), "the second start must leave the checkpoint alone");
            }
        }
        finally
        {
            resume.TrySetResult();
        }

        await first;
        int requestsByTheFirst = server.RequestCount;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.ReadAllBytes(Path.Combine(_dbPath, "state", "a42.sst")), Is.EqualTo(TestArchive.BuildFiles()["state/a42.sst"]), "the first start must finish");
            Assert.That(File.Exists(MarkerPath), Is.False, "the first start must remove its marker when it finishes");
            Assert.That(File.ReadAllText(checkpointPath), Is.EqualTo(nameof(SnapshotStage.Completed)), "the first start must complete its checkpoint");
        }

        // The lock is free again: a later start takes it and skips the initialized database as before.
        await new InitDatabaseSnapshot(_api, DrivesWithFreeSpace(long.MaxValue)).Execute(timeout.Token);
        Assert.That(server.RequestCount, Is.EqualTo(requestsByTheFirst), "a later start must skip the initialized database");
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

    [Test]
    [Platform(Include = "Linux")] // creating links needs a privilege on Windows
    public async Task Execute_LockPathIsALink_NeitherFollowsItNorStops()
    {
        // A dangling link that, followed, would create a file in the empty database directory and make it look populated.
        string target = Path.Combine(_dbPath, "lock-target");
        Directory.CreateDirectory(_dbPath);
        File.CreateSymbolicLink(LockPath, target);
        WriteSnapshotTar();
        AdvanceCheckpoint(SnapshotStage.Verified);
        TestLogger logger = new();
        _api.LogManager.Returns(new OneLoggerLogManager(new(logger)));

        await new InitDatabaseSnapshot(_api, DrivesWithFreeSpace(long.MaxValue)).Execute(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Path.Exists(target), Is.False, "the link must not be followed");
            Assert.That(new FileInfo(LockPath).LinkTarget, Is.EqualTo(target), "the link must stay");
            Assert.That(File.Exists(Path.Combine(_dbPath, "state.bin")), Is.True, "the start goes on without the lock, as when the lock file cannot be opened");
            Assert.That(logger.LogList.Any(static line => line.Contains("is a link, which the step does not follow")), Is.True, "the start must say it runs without the lock");
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
