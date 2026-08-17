// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Init.Snapshot.Test;

[TestFixture]
public class SnapshotHttpStreamTests
{
    private const int TestChunkSize = 16384;

    private FlakySnapshotServer _server = null!;

    [SetUp]
    public void SetUp() => _server = new FlakySnapshotServer();

    [TearDown]
    public void TearDown() => _server.Dispose();

    [TestCase(1000, TestName = "SmallerThanOneChunk")]
    [TestCase(2 * TestChunkSize, TestName = "ExactChunkMultiple")]
    [TestCase(100_000, TestName = "PartialLastChunk")]
    public async Task Read_ChunkedDownload_DeliversExactContentAndHash(int contentLength)
    {
        byte[] content = BuildContent(contentLength);
        _server.Content = content;

        (byte[] delivered, byte[] hash) = await DownloadAsync(connections: 3);

        Assert.That(delivered, Is.EqualTo(content), "a chunked download must deliver the exact source bytes in order");
        Assert.That(hash, Is.EqualTo(SHA256.HashData(content)), "the incremental hash must cover every byte exactly once");
    }

    [Test]
    public async Task Read_EveryRangeDroppedOnFirstAttempt_DeliversExactContent()
    {
        byte[] content = BuildContent(100_000);
        _server.Content = content;
        _server.DropFirstAttemptPerRangeAfterBytes = 5000;

        (byte[] delivered, byte[] hash) = await DownloadAsync(connections: 3);

        Assert.That(delivered, Is.EqualTo(content), "every chunk must survive a dropped connection and be re-fetched");
        Assert.That(hash, Is.EqualTo(SHA256.HashData(content)), "retried chunks must not be hashed twice");
    }

    [Test]
    public async Task Read_ServerWithoutRangeSupport_DeliversExactContent()
    {
        byte[] content = BuildContent(100_000);
        _server.Content = content;
        _server.SupportsRanges = false;

        (byte[] delivered, byte[] hash) = await DownloadAsync(connections: 3);

        Assert.That(delivered, Is.EqualTo(content), "the sequential fallback must deliver the exact source bytes");
        Assert.That(hash, Is.EqualTo(SHA256.HashData(content)), "the sequential fallback must hash every byte exactly once");
    }

    [Test]
    public async Task Read_ServerWithoutRangeSupportDropsConnections_ResumesBySkipping()
    {
        byte[] content = BuildContent(100_000);
        _server.Content = content;
        _server.SupportsRanges = false;
        _server.DropFirstAttemptPerRangeAfterBytes = 30_000;

        (byte[] delivered, byte[] hash) = await DownloadAsync(connections: 3);

        Assert.That(delivered, Is.EqualTo(content), "a resumed connection on a rangeless server must skip already delivered bytes, not replay them");
        Assert.That(hash, Is.EqualTo(SHA256.HashData(content)), "skipped bytes must not be hashed twice");
    }

    [Test]
    public void Read_SourceChangesMidDownload_ThrowsSnapshotSourceChanged()
    {
        _server.Content = BuildContent(100_000);
        _server.SwitchSourceAfterRequests(2, BuildContent(500), "\"v2\"");

        Assert.ThrowsAsync<SnapshotSourceChangedException>(
            () => DownloadAsync(connections: 1),
            "a snapshot replaced on the server mid-download must abort the stream instead of mixing two objects");
    }

    [Test]
    public void Read_SourceLengthChangesOnRangelessServerWithoutETag_ThrowsSnapshotSourceChanged()
    {
        _server.Content = BuildContent(100_000);
        _server.SupportsRanges = false;
        _server.ETag = null;
        _server.DropFirstAttemptPerRangeAfterBytes = 30_000;
        _server.SwitchSourceAfterRequests(2, BuildContent(50_000), null);

        Assert.ThrowsAsync<SnapshotSourceChangedException>(
            () => DownloadAsync(connections: 1),
            "a rotated source without an ETag must be detected by its length instead of splicing two objects together");
    }

    [Test]
    public void Read_ServerReturnsNotFoundMidDownload_ThrowsWithoutRetrying()
    {
        _server.Content = BuildContent(100_000);
        _server.FailWithNotFoundAfterRequests = 1;

        Assert.ThrowsAsync<HttpRequestException>(
            () => DownloadAsync(connections: 2),
            "a permanent HTTP error must abort the stream instead of retrying forever");
    }

    private async Task<(byte[] Delivered, byte[] Hash)> DownloadAsync(int connections)
    {
        SnapshotStreamSettings settings = new(connections, TestChunkSize, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(50));
        using SnapshotHttpClient client = new();
        SnapshotRemoteInfo remoteInfo = await client.ProbeAsync(_server.Url, CancellationToken.None);
        await using SnapshotHttpStream stream = new(client, _server.Url, remoteInfo, settings, LimboLogs.Instance, CancellationToken.None);
        using MemoryStream delivered = new();
        await Task.Run(() => stream.CopyTo(delivered));
        byte[] hash = await stream.FinishAsync(CancellationToken.None);
        return (delivered.ToArray(), hash);
    }

    private static byte[] BuildContent(int length)
    {
        byte[] content = new byte[length];
        new Random(42).NextBytes(content);
        return content;
    }
}
