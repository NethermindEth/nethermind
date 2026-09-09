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

        AssertDeliveredExactly(delivered, hash, content, "a chunked download must deliver the exact source bytes in order", "the incremental hash must cover every byte exactly once");
    }

    [Test]
    public async Task Read_EveryRangeDroppedOnFirstAttempt_DeliversExactContent()
    {
        byte[] content = BuildContent(100_000);
        _server.Content = content;
        _server.DropFirstAttemptPerRangeAfterBytes = 5000;

        (byte[] delivered, byte[] hash) = await DownloadAsync(connections: 3);

        AssertDeliveredExactly(delivered, hash, content, "every chunk must survive a dropped connection and be re-fetched", "retried chunks must not be hashed twice");
    }

    [Test]
    public async Task Read_ServerWithoutRangeSupport_DeliversExactContent()
    {
        byte[] content = BuildContent(100_000);
        _server.Content = content;
        _server.SupportsRanges = false;

        (byte[] delivered, byte[] hash) = await DownloadAsync(connections: 3);

        AssertDeliveredExactly(delivered, hash, content, "the sequential fallback must deliver the exact source bytes", "the sequential fallback must hash every byte exactly once");
    }

    [Test]
    public async Task Read_ServerWithoutRangeSupportDropsConnections_ResumesBySkipping()
    {
        byte[] content = BuildContent(100_000);
        _server.Content = content;
        _server.SupportsRanges = false;
        _server.DropFirstAttemptPerRangeAfterBytes = 30_000;

        (byte[] delivered, byte[] hash) = await DownloadAsync(connections: 3);

        AssertDeliveredExactly(delivered, hash, content, "a resumed connection on a rangeless server must skip already delivered bytes, not replay them", "skipped bytes must not be hashed twice");
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
    public void Read_ETagDisappearsMidDownloadWithoutChecksum_Throws()
    {
        _server.Content = BuildContent(100_000);
        _server.DropETagAfterRequests = 1;
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));

        Assert.ThrowsAsync<InvalidDataException>(
            () => DownloadAsync(connections: 1, computeChecksum: false, cancellationToken: timeout.Token),
            "a source that stops identifying itself must abort the stream, because nothing else would notice a same-size replacement");
    }

    [Test]
    public void Read_ServerNeverDeliversBytes_GivesUpInsteadOfRetryingForever()
    {
        _server.Content = BuildContent(100_000);
        _server.DropEveryAttemptAfterBytes = 0;
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));

        Assert.ThrowsAsync<IOException>(
            () => DownloadAsync(connections: 1, maxNoProgressRetries: 3, cancellationToken: timeout.Token),
            "a connection that accepts the range and then delivers nothing must be bounded, otherwise node startup hangs forever");
    }

    [TestCase("https://host/a", "http://host/b", TestName = "HttpsToHttp")]
    public void ResolveRedirect_Downgrade_Throws(string from, string to) =>
        Assert.Throws<IOException>(() => SnapshotHttpClient.ResolveRedirect(new Uri(from), new Uri(to)),
            "following a redirect off https would expose the headers the integrity policy relies on");

    [TestCase("https://host/a", "https://other/b", TestName = "HttpsToHttps")]
    [TestCase("http://host/a", "http://other/b", TestName = "HttpToHttp")]
    [TestCase("https://host/a", "/b", TestName = "RelativeKeepsScheme")]
    public void ResolveRedirect_NoDowngrade_Resolves(string from, string to) =>
        Assert.That(SnapshotHttpClient.ResolveRedirect(new Uri(from), new Uri(to, UriKind.RelativeOrAbsolute)).AbsoluteUri,
            Is.Not.Empty, "a redirect that keeps or raises the transport must be followed");

    [Test]
    public void Read_ServerReturnsNotFoundMidDownload_ThrowsWithoutRetrying()
    {
        _server.Content = BuildContent(100_000);
        _server.FailWithNotFoundAfterRequests = 1;
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));

        Assert.ThrowsAsync<HttpRequestException>(
            () => DownloadAsync(connections: 2, cancellationToken: timeout.Token),
            "a permanent HTTP error must abort the stream instead of retrying forever");
    }

    [Test]
    public async Task Read_ServerIgnoresOneRangeRequest_RetriesAndDeliversExactContent()
    {
        byte[] content = BuildContent(100_000);
        _server.Content = content;
        _server.IgnoreRangeOnce = true;

        (byte[] delivered, byte[] hash) = await DownloadAsync(connections: 2);

        AssertDeliveredExactly(delivered, hash, content, "a single 200 answer to a range request must be retried instead of corrupting the stream", "the retried chunk must be hashed exactly once");
    }

    [Test]
    public async Task Dispose_AfterFullDownload_ReleasesPooledBuffers()
    {
        byte[] content = BuildContent(100_000);
        _server.Content = content;
        SnapshotStreamSettings settings = new(3, TestChunkSize, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(30), ComputeChecksum: true);
        using SnapshotHttpClient client = new();
        SnapshotRemoteInfo remoteInfo = await client.ProbeAsync(_server.Url, CancellationToken.None);
        SnapshotHttpStream stream = new(client, _server.Url, remoteInfo, settings, LimboLogs.Instance, CancellationToken.None);
        using MemoryStream delivered = new();
        await Task.Run(() => stream.CopyTo(delivered));

        await stream.DisposeAsync();

        Assert.That(stream.PooledBufferCount, Is.EqualTo(0),
            "disposing the stream must release the pooled chunk buffers instead of keeping them reachable");
    }

    [Test]
    public async Task Read_ServerStallsOnce_DetectsStallAndResumes()
    {
        byte[] content = BuildContent(100_000);
        _server.Content = content;
        _server.HangOnceAfterBytes = 2000;

        (byte[] delivered, byte[] hash) = await DownloadAsync(connections: 2, stallTimeout: TimeSpan.FromMilliseconds(250));

        AssertDeliveredExactly(delivered, hash, content, "a connection that stops delivering bytes must be detected as stalled and the chunk re-fetched", "bytes received before the stall must be hashed exactly once");
    }

    private async Task<(byte[] Delivered, byte[] Hash)> DownloadAsync(
        int connections, TimeSpan? stallTimeout = null, bool computeChecksum = true, int maxNoProgressRetries = 20, CancellationToken cancellationToken = default)
    {
        SnapshotStreamSettings settings = new(connections, TestChunkSize, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(50),
            stallTimeout ?? TimeSpan.FromSeconds(30), computeChecksum, maxNoProgressRetries);
        using SnapshotHttpClient client = new();
        SnapshotRemoteInfo remoteInfo = await client.ProbeAsync(_server.Url, cancellationToken);
        await using SnapshotHttpStream stream = new(client, _server.Url, remoteInfo, settings, LimboLogs.Instance, cancellationToken);
        using MemoryStream delivered = new();
        await Task.Run(() => stream.CopyTo(delivered));
        byte[] hash = (await stream.FinishAsync(cancellationToken))!;
        return (delivered.ToArray(), hash);
    }

    private static void AssertDeliveredExactly(byte[] delivered, byte[] hash, byte[] expected, string deliveryReason, string hashReason)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(delivered, Is.EqualTo(expected), deliveryReason);
            Assert.That(hash, Is.EqualTo(SHA256.HashData(expected)), hashReason);
        }
    }

    private static byte[] BuildContent(int length)
    {
        byte[] content = new byte[length];
        new Random(42).NextBytes(content);
        return content;
    }
}
