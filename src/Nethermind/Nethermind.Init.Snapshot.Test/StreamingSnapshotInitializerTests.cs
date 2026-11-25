// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using System.Security.Cryptography;
using Nethermind.Core.Test.IO;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Init.Snapshot.Test;

[TestFixture]
public class StreamingSnapshotInitializerTests
{
    private const int TestChunkSize = 16384;

    private FlakySnapshotServer _server = null!;
    private TempPath _tempDir = null!;
    private string _dbPath = null!;
    private SnapshotConfig _config = null!;

    [SetUp]
    public void SetUp()
    {
        _server = new FlakySnapshotServer();
        _tempDir = TempPath.GetTempDirectory();
        _dbPath = Path.Combine(_tempDir.Path, "db");
        string snapshotDirectory = Path.Combine(_tempDir.Path, "snapshot");
        Directory.CreateDirectory(snapshotDirectory);
        _config = new SnapshotConfig
        {
            Enabled = true,
            DownloadUrl = _server.Url,
            SnapshotDirectory = snapshotDirectory,
            SnapshotFileName = "snapshot.tar.zst",
            Streaming = true,
        };
    }

    [TearDown]
    public void TearDown()
    {
        _server.Dispose();
        _tempDir.Dispose();
    }

    [Test]
    public async Task InitializeAsync_ValidSnapshot_ExtractsDatabaseAndCompletesCheckpoint()
    {
        Dictionary<string, byte[]> files = TestArchive.BuildFiles();
        byte[] archive = TestArchive.BuildTarZst(files);
        _server.Content = archive;
        _config.Checksum = Convert.ToHexString(SHA256.HashData(archive));
        string staleArchivePath = Path.Combine(_config.SnapshotDirectory, _config.SnapshotFileName);
        File.WriteAllBytes(staleArchivePath, [1, 2, 3]);
        SnapshotCheckpoint checkpoint = CreateCheckpoint();

        await CreateInitializer().InitializeAsync(checkpoint, CancellationToken.None);

        AssertExtracted(files);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(checkpoint.Read(), Is.EqualTo(SnapshotStage.Completed), "a verified extraction must complete the checkpoint");
            Assert.That(File.Exists(staleArchivePath), Is.False, "a stale archive file must be deleted since streaming never uses it");
        }
    }

    [Test]
    public async Task InitializeAsync_EveryRangeDroppedOnFirstAttempt_ExtractsDatabase()
    {
        Dictionary<string, byte[]> files = TestArchive.BuildFiles();
        byte[] archive = TestArchive.BuildTarZst(files);
        _server.Content = archive;
        _server.DropFirstAttemptPerRangeAfterBytes = 2000;
        _config.Checksum = Convert.ToHexString(SHA256.HashData(archive));
        SnapshotCheckpoint checkpoint = CreateCheckpoint();

        await CreateInitializer().InitializeAsync(checkpoint, CancellationToken.None);

        AssertExtracted(files);
        Assert.That(checkpoint.Read(), Is.EqualTo(SnapshotStage.Completed), "dropped connections must be resumed without corrupting the extraction");
    }

    [Test]
    public async Task InitializeAsync_ChecksumMismatch_StopsAndKeepsWhatItExtracted()
    {
        byte[] archive = TestArchive.BuildTarZst(TestArchive.BuildFiles());
        _server.Content = archive;
        _config.Checksum = Convert.ToHexString(SHA256.HashData([9, 9, 9]));
        SnapshotCheckpoint checkpoint = CreateCheckpoint();

        IOException exception = (await Assert.ThrowsAsync<IOException>(() => CreateInitializer().InitializeAsync(checkpoint, CancellationToken.None),
            "the node must not start on a database extracted from an unverified snapshot"))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception.Message, Does.Contain("delete its contents by hand"), "the error must say what to do");
            Assert.That(File.Exists(Path.Combine(_dbPath, "state", "a42.sst")), Is.True, "what the stream extracted must stay for the operator");
            Assert.That(File.Exists(MarkerPath), Is.True, "the marker must stay, so a later start stops too");
            Assert.That(checkpoint.Read(), Is.EqualTo(SnapshotStage.Started), "the checkpoint must not advance when the checksum fails");
        }
    }

    [Test]
    public async Task InitializeAsync_CorruptArchive_StopsAndKeepsTheMarker()
    {
        byte[] garbage = new byte[50_000];
        new Random(42).NextBytes(garbage);
        _server.Content = garbage;
        _config.Checksum = Convert.ToHexString(SHA256.HashData(garbage));
        SnapshotCheckpoint checkpoint = CreateCheckpoint();

        Assert.That(await Assert.CatchAsync(() => CreateInitializer().InitializeAsync(checkpoint, CancellationToken.None)), Is.Not.Null,
            "a corrupt archive must stop the start");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.Exists(MarkerPath), Is.True, "the marker must stay, so a later start stops too");
            Assert.That(checkpoint.Read(), Is.EqualTo(SnapshotStage.Started), "the checkpoint must not advance when extraction fails");
        }
    }

    [Test]
    public async Task InitializeAsync_ArchiveWithSymlinkEntry_StopsWithoutCreatingTheLink()
    {
        byte[] archive = TestArchive.BuildTarZstWithSymlink();
        _server.Content = archive;
        _config.Checksum = Convert.ToHexString(SHA256.HashData(archive));
        SnapshotCheckpoint checkpoint = CreateCheckpoint();

        await Assert.ThatAsync(() => CreateInitializer().InitializeAsync(checkpoint, CancellationToken.None),
            Throws.InstanceOf<IOException>().With.Message.Contains("unsupported type"),
            "an archive with link entries must be rejected, because links can escape the database directory");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Path.Exists(Path.Combine(_dbPath, "escape")), Is.False, "the link entry must not be extracted");
            Assert.That(File.Exists(MarkerPath), Is.True, "the marker must stay, so a later start stops too");
            Assert.That(checkpoint.Read(), Is.EqualTo(SnapshotStage.Started), "the checkpoint must not advance when extraction is rejected");
        }
    }

    [Test]
    public async Task InitializeAsync_NoStrongETagWithoutChecksum_Throws()
    {
        _server.Content = TestArchive.BuildTarZst(TestArchive.BuildFiles());
        _server.ETag = null;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateInitializer().InitializeAsync(CreateCheckpoint(), CancellationToken.None),
            "without an entity tag and without a checksum a snapshot replaced by one of the same size would be undetectable, so streaming must refuse to start");
    }

    [Test]
    public async Task InitializeAsync_SourceRotatesToEqualLengthWithoutETag_DoesNotCompleteTheCheckpoint()
    {
        byte[] archive = TestArchive.BuildTarZst(TestArchive.BuildFiles());
        byte[] rotated = (byte[])archive.Clone();
        rotated[^1000] ^= 0xFF;
        _server.Content = archive;
        _server.ETag = null;
        _server.SwitchSourceAfterRequests(2, rotated, null);
        _config.Checksum = Convert.ToHexString(SHA256.HashData(archive));
        SnapshotCheckpoint checkpoint = CreateCheckpoint();

        await Assert.ThatAsync(() => CreateInitializer(connections: 1).InitializeAsync(checkpoint, CancellationToken.None),
            Throws.InstanceOf<IOException>(), "the node must not start on the database extracted from the mixed stream");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(checkpoint.Read(), Is.EqualTo(SnapshotStage.Started),
                "a source rotated to equal-length content with no entity tag must be caught by the checksum instead of completing");
            Assert.That(File.Exists(MarkerPath), Is.True, "the database extracted from the mixed stream must stay marked");
        }
    }

    [Test]
    public async Task InitializeAsync_UnknownLengthWithoutChecksum_Throws()
    {
        _server.Content = TestArchive.BuildTarZst(TestArchive.BuildFiles());
        _server.SupportsRanges = false;
        _server.OmitContentLength = true;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateInitializer().InitializeAsync(CreateCheckpoint(), CancellationToken.None),
            "without a length and without a checksum a truncated download would be undetectable, so streaming must refuse to start");
    }

    [Test]
    public async Task InitializeAsync_ArchiveNotMatchingStripComponents_KeepsTheMarkerAndThrows()
    {
        byte[] archive = TestArchive.BuildTarZstWithoutTopLevelDirectory();
        _server.Content = archive;
        _config.Checksum = Convert.ToHexString(SHA256.HashData(archive));
        SnapshotCheckpoint checkpoint = CreateCheckpoint();

        InvalidOperationException exception = (await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateInitializer().InitializeAsync(checkpoint, CancellationToken.None),
            "an extraction that produced no files is a configuration error and must fail startup in both modes"))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception.Message, Does.Contain("StripComponents"),
                "the failure must point the operator at the strip configuration");
            Assert.That(File.Exists(MarkerPath), Is.True,
                "the database directory must stay marked, so a later start stops too");
            Assert.That(checkpoint.Read(), Is.EqualTo(SnapshotStage.Started),
                "the checkpoint must not advance when nothing was extracted");
        }
    }

    [Test]
    public async Task InitializeAsync_ZipArchiveConfigured_Throws()
    {
        _config.SnapshotFileName = "snapshot.zip";

        await Assert.ThrowsAsync<NotSupportedException>(
            () => CreateInitializer().InitializeAsync(CreateCheckpoint(), CancellationToken.None),
            "zip archives cannot be extracted from a stream and must be rejected upfront");
    }

    [Test]
    public async Task InitializeAsync_SourceChanges_StopsWithoutRestarting()
    {
        byte[] oldArchive = TestArchive.BuildTarZst(TestArchive.BuildFiles(seed: 42));
        byte[] newArchive = TestArchive.BuildTarZst(TestArchive.BuildFiles(seed: 43));
        Assert.That(oldArchive.Length, Is.GreaterThan(TestChunkSize), "precondition: the old archive must span multiple chunks so the switch happens mid-download");
        _server.Content = oldArchive;
        _server.SwitchSourceAfterRequests(2, newArchive, "\"v2\"");
        _config.Checksum = Convert.ToHexString(SHA256.HashData(newArchive));
        SnapshotCheckpoint checkpoint = CreateCheckpoint();

        await Assert.CatchAsync<SnapshotSourceChangedException>(() => CreateInitializer(connections: 1).InitializeAsync(checkpoint, CancellationToken.None),
            "a restart would extract the new source over what the first attempt wrote");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.Exists(Path.Combine(_dbPath, "state/a43.sst")), Is.False, "the new source must not be extracted over the partial data");
            Assert.That(File.Exists(MarkerPath), Is.True, "the marker must stay, so a later start stops too");
            Assert.That(checkpoint.Read(), Is.EqualTo(SnapshotStage.Started), "the checkpoint must not advance when the download never completes");
        }
    }

    [Test]
    public async Task InitializeAsync_ProbeFailsTransientlyOnce_RetriesAndCompletes([Values(408, 429, 500, 503)] int statusCode)
    {
        Dictionary<string, byte[]> files = TestArchive.BuildFiles();
        byte[] archive = TestArchive.BuildTarZst(files);
        _server.Content = archive;
        _server.FailRequestWithStatus = (1, statusCode);
        _config.Checksum = Convert.ToHexString(SHA256.HashData(archive));
        SnapshotCheckpoint checkpoint = CreateCheckpoint();

        await CreateInitializer().InitializeAsync(checkpoint, CancellationToken.None);

        AssertExtracted(files);
        Assert.That(checkpoint.Read(), Is.EqualTo(SnapshotStage.Completed),
            "a transient probe failure must be retried instead of failing node startup");
    }

    [Test]
    public async Task InitializeAsync_InsufficientDiskSpace_Throws()
    {
        _server.Content = TestArchive.BuildTarZst(TestArchive.BuildFiles());
        IDriveInfo drive = Substitute.For<IDriveInfo>();
        drive.AvailableFreeSpace.Returns(10);
        drive.RootDirectory.FullName.Returns("/db-drive");

        IOException exception = (await Assert.ThrowsAsync<IOException>(
            () => CreateInitializer(drives: [drive]).InitializeAsync(CreateCheckpoint(), CancellationToken.None),
            "the disk space check must fail before any byte is extracted"))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception.Message, Does.Contain("Insufficient disk space"), "the failure must be the disk space guard, not a download error");
            Assert.That(Directory.Exists(_dbPath), Is.False, "nothing must be extracted when free space is insufficient");
        }
    }

    [Test]
    public async Task InitializeAsync_MarkerCannotBeWritten_ThrowsWithoutDeletingAnything()
    {
        _server.Content = TestArchive.BuildTarZst(TestArchive.BuildFiles());
        _config.Checksum = Convert.ToHexString(SHA256.HashData(_server.Content));
        string existing = Path.Combine(_dbPath, "state", "000001.sst");
        Directory.CreateDirectory(Path.GetDirectoryName(existing)!);
        File.WriteAllBytes(existing, [1, 2, 3]);
        // A directory in the marker's place makes the marker write fail.
        Directory.CreateDirectory(Path.Combine(_dbPath, ".nethermind-snapshot-extracting"));

        await Assert.ThatAsync(() => CreateInitializer().InitializeAsync(CreateCheckpoint(), CancellationToken.None),
            Throws.InstanceOf<IOException>().Or.InstanceOf<UnauthorizedAccessException>());

        Assert.That(File.Exists(existing), Is.True, "a marker that cannot be written must stop the start before anything is deleted");
    }

    private string MarkerPath => Path.Combine(_dbPath, ".nethermind-snapshot-extracting");

    private StreamingSnapshotInitializer CreateInitializer(int connections = 2, IDriveInfo[]? drives = null) =>
        new(_config, _server.Url, _dbPath, drives ?? [],
            new SnapshotStreamSettings(connections, TestChunkSize, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(30)),
            LimboLogs.Instance);

    private SnapshotCheckpoint CreateCheckpoint() => new(_config, LimboLogs.Instance);

    private void AssertExtracted(IReadOnlyDictionary<string, byte[]> files)
    {
        foreach ((string name, byte[] data) in files)
        {
            string path = Path.Combine(_dbPath, name);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(File.Exists(path), Is.True, $"extracted file '{name}' must exist because it is part of the archive");
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(data), $"extracted file '{name}' must match the archived content byte for byte");
            }
        }
    }
}
