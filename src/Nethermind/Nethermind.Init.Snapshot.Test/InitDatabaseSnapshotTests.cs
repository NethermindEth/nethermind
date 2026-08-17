// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Formats.Tar;
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
        Assert.That(freeSpace, Is.GreaterThanOrEqualTo(InitDatabaseSnapshot.GetRequiredSpaceForExtraction(snapshotSize)),
            "precondition: free space must cover the extraction estimate");
        Assert.That(freeSpace, Is.LessThan((long)(snapshotSize * 2.5)),
            "precondition: free space must be below the legacy 2.5x requirement to prove the regression is fixed");
        InitDatabaseSnapshot step = new(_api, DrivesWithFreeSpace(freeSpace));

        await step.Execute(CancellationToken.None);

        Assert.That(File.Exists(Path.Combine(_dbPath, "state.bin")), Is.True,
            "the snapshot content should be extracted into the database directory");
        Assert.That(File.Exists(_snapshotPath), Is.False,
            "the snapshot archive should be deleted after a successful extraction");
    }

    [Test]
    public void Execute_FreeSpaceBelowExtractionEstimate_ThrowsIOException()
    {
        long snapshotSize = WriteSnapshotTar();
        AdvanceCheckpoint(SnapshotStage.Verified);
        InitDatabaseSnapshot step = new(_api, DrivesWithFreeSpace(snapshotSize));

        IOException exception = Assert.ThrowsAsync<IOException>(() => step.Execute(CancellationToken.None))!;

        Assert.That(exception.Message, Does.Contain("Insufficient disk space"),
            "the extraction should be rejected when free space is below the extraction estimate");
        Assert.That(Directory.Exists(_dbPath), Is.False,
            "nothing should be extracted when free space is insufficient");
    }

    [Test]
    public void Execute_FreeSpaceBelowDownloadRequirement_ThrowsIOExceptionBeforeDownloading()
    {
        using SnapshotServer server = SnapshotServer.Start(contentLength: 1_000_000);
        _snapshotConfig.DownloadUrl = server.Url;
        InitDatabaseSnapshot step = new(_api, DrivesWithFreeSpace(1_000_000));

        IOException exception = Assert.ThrowsAsync<IOException>(() => step.Execute(CancellationToken.None))!;

        Assert.That(exception.Message, Does.Contain("Insufficient disk space"),
            "the download should be rejected when free space cannot fit the snapshot and its extraction");
        Assert.That(File.Exists(_snapshotPath), Is.False,
            "no bytes should be downloaded when free space is insufficient");
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
        InitDatabaseSnapshot step = new(_api, DrivesWithFreeSpace(long.MaxValue));

        await step.Execute(CancellationToken.None);

        Assert.That(File.Exists(Path.Combine(_dbPath, "state/a42.sst")), Is.True,
            "the streaming path must extract the snapshot into the database directory");
    }

    [TestCase(0, TestName = "Zero")]
    [TestCase(17, TestName = "AboveMaximum")]
    public void Execute_StreamingConnectionsOutOfRange_Throws(int connections)
    {
        _snapshotConfig.Streaming = true;
        _snapshotConfig.StreamingConnections = connections;
        InitDatabaseSnapshot step = new(_api, DrivesWithFreeSpace(long.MaxValue));

        Assert.ThrowsAsync<InvalidOperationException>(() => step.Execute(CancellationToken.None),
            "a connection count outside 1-16 must be rejected before any network activity");
    }

    [Test]
    public async Task Execute_StreamingEnabledWithDownloadedArchive_ExtractsWithoutStreaming()
    {
        WriteSnapshotTar();
        AdvanceCheckpoint(SnapshotStage.Verified);
        _snapshotConfig.Streaming = true;
        InitDatabaseSnapshot step = new(_api, DrivesWithFreeSpace(long.MaxValue));

        await step.Execute(CancellationToken.None);

        Assert.That(File.Exists(Path.Combine(_dbPath, "state.bin")), Is.True,
            "an already downloaded archive must be extracted through the two-phase path instead of being deleted and re-streamed");
        Assert.That(File.Exists(_snapshotPath), Is.False,
            "the archive must be deleted after a successful two-phase extraction");
    }

    [TestCase(1_000, 0, 2_500, TestName = "FreshDownload")]
    [TestCase(1_000, 400, 2_100, TestName = "ResumedDownload")]
    public void GetRequiredSpaceForDownload_ForGivenSizes_AddsRemainingBytesToExtractionEstimate(
        long totalSize, long existingSize, long expected) =>
        Assert.That(InitDatabaseSnapshot.GetRequiredSpaceForDownload(totalSize, existingSize), Is.EqualTo(expected),
            "the pre-download requirement should be the remaining bytes plus the extraction estimate of the full snapshot");

    private long WriteSnapshotTar()
    {
        using (FileStream fileStream = File.Create(_snapshotPath))
        using (TarWriter tarWriter = new(fileStream))
        {
            tarWriter.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "data"));
            PaxTarEntry fileEntry = new(TarEntryType.RegularFile, "data/state.bin")
            {
                DataStream = new MemoryStream(new byte[SnapshotPayloadSize])
            };
            tarWriter.WriteEntry(fileEntry);
        }

        return new FileInfo(_snapshotPath).Length;
    }

    private void AdvanceCheckpoint(SnapshotStage stage) =>
        new SnapshotCheckpoint(_snapshotConfig, LimboLogs.Instance).Advance(stage);

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
