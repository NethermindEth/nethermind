// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using System.Net;
using System.Security.Cryptography;
using Nethermind.Api;
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

    [TestCase(false, TestName = "Execute_LinkedSubdirectoryWithMissingTarget_StopsAndKeepsTheLink")]
    [Platform(Include = "Linux")] // creating links needs a privilege on Windows
    public async Task Execute_LinkWithMissingTarget_StopsAndKeepsTheLink(bool linkDatabaseDirectory)
    {
        string target = Path.Combine(_tempDir.Path, "other", linkDatabaseDirectory ? "db" : "state");
        Directory.CreateDirectory(target);
        string link = linkDatabaseDirectory ? _dbPath : Path.Combine(_dbPath, "state");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        Directory.CreateSymbolicLink(link, target);
        InitDatabaseSnapshot step = new(_api, DrivesWithFreeSpace(long.MaxValue));
        await FailExtraction(step);
        // The linked disk is not mounted at the next start.
        string unmounted = target + "-unmounted";
        Directory.Move(target, unmounted);
        File.WriteAllBytes(_snapshotPath, TestArchive.BuildTar(TestArchive.BuildFiles(seed: 43)));

        IOException exception = (await Assert.CatchAsync<IOException>(() => step.Execute(CancellationToken.None)))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception.Message, Does.Contain("missing or cannot be read"), "the start must stop at a link it cannot follow");
            Assert.That(new DirectoryInfo(link).LinkTarget, Is.EqualTo(target), "the link must be neither removed nor replaced by a plain directory");
            Assert.That(File.Exists(Path.Combine(unmounted, linkDatabaseDirectory ? "state/a42.sst" : "a42.sst")), Is.True,
                "the content behind the link must stay for the cleanup after the disk is back");
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

    private void AdvanceCheckpoint(SnapshotStage stage) =>
        new SnapshotCheckpoint(_snapshotConfig, LimboLogs.Instance).Advance(stage);

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
