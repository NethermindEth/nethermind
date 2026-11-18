// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Stats;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using NSubstitute;
using NUnit.Framework;

using System;
using System.Collections.Generic;
using System.Reflection;
using Nethermind.Core.Collections;
using Nethermind.Db;
using Nethermind.Trie;

namespace Nethermind.Synchronization.Test.FastSync;

public class TreeSyncTests : StateSyncFeedTestsBase
{

    [Test]
    public async Task Reset_owns_removed_response_until_its_buffer_is_returned([Values] bool lateResponse)
    {
        RemoteDbContext remote = new(_logManager);
        remote.StateTree.Set(Keccak.Zero, new Account(1, 2));
        remote.StateTree.Commit();
        INodeStatsManager stats = Substitute.For<INodeStatsManager>();
        await using IContainer container = PrepareDownloader(remote, configureBuilder: builder => builder.AddSingleton(stats));
        SafeContext ctx = container.Resolve<SafeContext>();
        ISyncDownloader<StateSyncBatch> downloader = container.Resolve<ISyncDownloader<StateSyncBatch>>();
        PeerInfo peer = new(ctx.SyncPeerMocks[0]);
        ctx.TreeFeed.ResetStateRootToBestSuggested();
        StateSyncBatch batch = (await ctx.TreeFeed.PrepareRequest())!;
        if (lateResponse) ctx.TreeFeed.ResetStateRoot();
        await downloader.Dispatch(peer, batch, CancellationToken.None);
        using CancellationTokenSource watchdog = new(TimeSpan.FromSeconds(10));
        stats.ClearReceivedCalls();
        SyncResponseHandlingResult? result = null;
        DisposalBarrier response = new(batch.Responses!, () =>
            result = Task.Run(() => ctx.Feed.HandleResponse(batch, peer)).WaitAsync(watchdog.Token).GetAwaiter().GetResult());
        batch.Responses = response;

        if (lateResponse) ctx.Feed.HandleResponse(batch, peer);
        else ctx.TreeFeed.ResetStateRoot();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(SyncResponseHandlingResult.Emptish));
            Assert.That(response.ReadsDuringDisposal, Is.Zero);
            Assert.That(response.DisposeCalls, Is.EqualTo(1));
        }
        stats.DidNotReceive().ReportTransferSpeedEvent(peer.SyncPeer.Node, TransferSpeedType.NodeData, Arg.Any<long>());
        StateSyncBatch retry = (await ctx.TreeFeed.PrepareRequest())!;
        await downloader.Dispatch(peer, retry, CancellationToken.None);
        Assert.That(ctx.Feed.HandleResponse(retry, peer), Is.EqualTo(SyncResponseHandlingResult.OK));
        stats.Received(1).ReportTransferSpeedEvent(peer.SyncPeer.Node, TransferSpeedType.NodeData, retry.ValidatedBytes / Math.Max(1, retry.ResponseMilliseconds));
        Assert.That(ctx.TreeFeed.IsRootComplete, Is.True);
        container.Resolve<IStateSyncTestOperation>().CompareTrees(remote, _logger, "reset recovery");
    }

    [Test]
    public async Task Small_response_distinguishes_missing_data_from_wrong_hash([Values(0, 1, 2, 3)] int responseKind)
    {
        RemoteDbContext remote = new(_logManager);
        remote.StateTree.Set(Keccak.Zero, new Account(1, 2));
        remote.StateTree.Commit();
        INodeStatsManager stats = Substitute.For<INodeStatsManager>();
        await using IContainer container = PrepareDownloader(remote, configureBuilder: builder => builder.AddSingleton(stats));
        SafeContext ctx = container.Resolve<SafeContext>();
        ISyncDownloader<StateSyncBatch> downloader = container.Resolve<ISyncDownloader<StateSyncBatch>>();
        PeerInfo peer = new(ctx.SyncPeerMocks[0]);
        ctx.TreeFeed.ResetStateRootToBestSuggested();
        StateSyncBatch batch = (await ctx.TreeFeed.PrepareRequest())!;
        Assert.That(batch.RequestedNodes!.Count, Is.EqualTo(1));
        await downloader.Dispatch(peer, batch, CancellationToken.None);
        if (responseKind != 3)
        {
            batch.Responses!.Dispose();
            batch.Responses = new ByteArrayListAdapter(new ArrayPoolList<byte[]>(responseKind switch
            {
                0 => [],
                1 => [Array.Empty<byte>()],
                _ => [new byte[] { 0xff }]
            }));
        }
        long expectedBytes = responseKind == 3 ? batch.Responses![0].Length : 0;
        stats.ClearReceivedCalls();
        Assert.That(ctx.Feed.HandleResponse(batch, peer), Is.EqualTo(responseKind switch
        {
            2 => SyncResponseHandlingResult.LesserQuality,
            3 => SyncResponseHandlingResult.OK,
            _ => SyncResponseHandlingResult.NoProgress
        }));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(batch.ValidatedBytes, Is.EqualTo(expectedBytes));
            stats.Received(1).ReportTransferSpeedEvent(peer.SyncPeer.Node, TransferSpeedType.NodeData, expectedBytes / Math.Max(1, batch.ResponseMilliseconds));
        }
        if (responseKind != 3)
        {
            StateSyncBatch retry = (await ctx.TreeFeed.PrepareRequest())!;
            Assert.That(retry.RequestedNodes![0].Hash, Is.EqualTo(remote.StateTree.RootHash));
            await downloader.Dispatch(peer, retry, CancellationToken.None);
            Assert.That(ctx.Feed.HandleResponse(retry, peer), Is.EqualTo(SyncResponseHandlingResult.OK));
        }
        Assert.That(ctx.TreeFeed.IsRootComplete, Is.True);
        container.Resolve<IStateSyncTestOperation>().CompareTrees(remote, _logger, "small response recovery");
    }

    [TestCase(new[] { 0, 1 }, new[] { 1 }, new[] { 0 }, SyncResponseHandlingResult.OK, 1)]
    [TestCase(new[] { 0, 0, 1 }, new[] { 0, 0, 1 }, new int[0], SyncResponseHandlingResult.OK, 3)]
    [TestCase(new[] { 0, 1, 2 }, new[] { 2, 1, 0 }, new[] { 0, 1 }, SyncResponseHandlingResult.LesserQuality, 1)]
    [TestCase(new[] { 0 }, new[] { 3 }, new[] { 0 }, SyncResponseHandlingResult.LesserQuality, 0)]
    [TestCase(new[] { 0 }, new[] { 0, 0, 0 }, new int[0], SyncResponseHandlingResult.LesserQuality, 1)]
    public async Task Ordered_responses_consume_requested_occurrences_and_retry_omissions(
        int[] requested, int[] returned, int[] missing, SyncResponseHandlingResult expected, int validatedCount)
    {
        RemoteDbContext remote = new(_logManager);
        remote.StateTree.Set(Keccak.Zero, new Account(1, 2));
        remote.StateTree.Commit();
        INodeStatsManager stats = Substitute.For<INodeStatsManager>();
        await using IContainer container = PrepareDownloader(remote, configureBuilder: builder => builder.AddSingleton(stats));
        SafeContext ctx = container.Resolve<SafeContext>();
        PeerInfo peer = new(ctx.SyncPeerMocks[0]);
        ctx.TreeFeed.ResetStateRootToBestSuggested();
        StateSyncBatch batch = (await ctx.TreeFeed.PrepareRequest())!;
        byte[][] codes = [[1], [2], [3], [0xff]];
        batch.RequestedNodes!.Clear();
        foreach (int index in requested)
            batch.RequestedNodes.Add(new StateSyncItem(Keccak.Compute(codes[index]), null, TreePath.Empty, NodeDataType.Code));
        batch.Responses = new ByteArrayListAdapter(new ArrayPoolList<byte[]>(returned.Length, returned.Select(index => codes[index])));
        batch.ResponseMilliseconds = 1;
        stats.ClearReceivedCalls();

        Assert.That(ctx.Feed.HandleResponse(batch, peer), Is.EqualTo(expected));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(batch.ValidatedBytes, Is.EqualTo(validatedCount));
            stats.Received(1).ReportTransferSpeedEvent(peer.SyncPeer.Node, TransferSpeedType.NodeData, validatedCount);
            Assert.That(typeof(TreeSync).GetField("_hintsToResetRoot", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(ctx.TreeFeed), Is.Zero);
        }
        List<Hash256> retried = [];
        for (int attempt = 0; attempt <= missing.Length; attempt++)
        {
            using StateSyncBatch? retry = await ctx.TreeFeed.PrepareRequest();
            if (retry is null) break;
            retried.AddRange(retry.RequestedNodes!.Select(item => item.Hash));
        }
        Assert.That(retried, Is.EquivalentTo(missing.Select(index => Keccak.Compute(codes[index]))));
        IDb codeDb = container.ResolveKeyed<IDb>(DbNames.Code);
        for (int index = 0; index < codes.Length; index++)
        {
            bool accepted = returned.Take(validatedCount).Contains(index);
            Assert.That(codeDb.Get(Keccak.Compute(codes[index]).Bytes), accepted ? Is.EqualTo(codes[index]) : Is.Null);
        }
    }

    [Test]
    public async Task Unassigned_and_timeout_responses_preserve_retry_and_sample_only_assigned_peer([Values] bool assigned)
    {
        RemoteDbContext remote = new(_logManager);
        remote.StateTree.Set(Keccak.Zero, new Account(1, 2));
        remote.StateTree.Commit();
        INodeStatsManager stats = Substitute.For<INodeStatsManager>();
        await using IContainer container = PrepareDownloader(remote, configureBuilder: builder => builder.AddSingleton(stats));
        SafeContext ctx = container.Resolve<SafeContext>();
        PeerInfo peer = new(ctx.SyncPeerMocks[0]);
        ctx.TreeFeed.ResetStateRootToBestSuggested();
        StateSyncBatch batch = (await ctx.TreeFeed.PrepareRequest())!;
        stats.ClearReceivedCalls();

        Assert.That(ctx.Feed.HandleResponse(batch, assigned ? peer : null), Is.EqualTo(assigned
            ? SyncResponseHandlingResult.LesserQuality : SyncResponseHandlingResult.NotAssigned));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(batch.ValidatedBytes, Is.Zero);
            stats.Received(assigned ? 1 : 0).ReportTransferSpeedEvent(peer.SyncPeer.Node, TransferSpeedType.NodeData, 0);
        }
        using StateSyncBatch? retry = await ctx.TreeFeed.PrepareRequest();
        Assert.That(retry, Is.Not.Null);
        Assert.That(retry!.RequestedNodes![0].Hash, Is.EqualTo(remote.StateTree.RootHash));
    }

    [Test]
    public async Task Null_batch_returns_internal_error_without_sample()
    {
        RemoteDbContext remote = new(_logManager);
        INodeStatsManager stats = Substitute.For<INodeStatsManager>();
        await using IContainer container = PrepareDownloader(remote, configureBuilder: builder => builder.AddSingleton(stats));
        SafeContext ctx = container.Resolve<SafeContext>();
        PeerInfo peer = new(ctx.SyncPeerMocks[0]);
        stats.ClearReceivedCalls();

        Assert.That(ctx.Feed.HandleResponse(null!, peer), Is.EqualTo(SyncResponseHandlingResult.InternalError));
        stats.DidNotReceive().ReportTransferSpeedEvent(peer.SyncPeer.Node, TransferSpeedType.NodeData, Arg.Any<long>());
    }

    [Test]
    public async Task Local_code_write_failure_records_no_sample([Values] bool fail)
    {
        byte[][] codes = [[1], [2]];
        WriteFailureDb codeDb = new(fail ? Keccak.Compute(codes[1]) : null);
        RemoteDbContext remote = new(_logManager);
        remote.StateTree.Set(Keccak.Zero, new Account(1, 2));
        remote.StateTree.Commit();
        INodeStatsManager stats = Substitute.For<INodeStatsManager>();
        await using IContainer container = PrepareDownloader(remote, configureBuilder: builder => builder
            .AddSingleton(stats)
            .AddKeyedSingleton<IDb>(DbNames.Code, codeDb));
        SafeContext ctx = container.Resolve<SafeContext>();
        PeerInfo peer = new(ctx.SyncPeerMocks[0]);
        ctx.TreeFeed.ResetStateRootToBestSuggested();
        StateSyncBatch batch = (await ctx.TreeFeed.PrepareRequest())!;
        batch.RequestedNodes!.Clear();
        foreach (byte[] code in codes)
            batch.RequestedNodes.Add(new StateSyncItem(Keccak.Compute(code), null, TreePath.Empty, NodeDataType.Code));
        batch.Responses = new ByteArrayListAdapter(new ArrayPoolList<byte[]>(codes.Length, codes));
        batch.ResponseMilliseconds = 1;
        stats.ClearReceivedCalls();

        Assert.That(ctx.Feed.HandleResponse(batch, peer), Is.EqualTo(fail
            ? SyncResponseHandlingResult.InternalError : SyncResponseHandlingResult.OK));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(batch.ValidatedBytes, Is.EqualTo(fail ? 1 : 2));
            if (fail) stats.DidNotReceive().ReportTransferSpeedEvent(peer.SyncPeer.Node, TransferSpeedType.NodeData, Arg.Any<long>());
            else stats.Received(1).ReportTransferSpeedEvent(peer.SyncPeer.Node, TransferSpeedType.NodeData, 2);
            Assert.That(codeDb.Get(Keccak.Compute(codes[0]).Bytes), Is.EqualTo(codes[0]));
            Assert.That(codeDb.Get(Keccak.Compute(codes[1]).Bytes), fail ? Is.Null : Is.EqualTo(codes[1]));
        }
    }

    private sealed class WriteFailureDb(Hash256? failingKey) : MemDb
    {
        public override void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
        {
            if (failingKey is not null && key.SequenceEqual(failingKey.Bytes))
                throw new InvalidOperationException("code store unavailable");
            base.Set(key, value, flags);
        }
    }

    private sealed class DisposalBarrier(IByteArrayList inner, Action duringDisposal) : IByteArrayList
    {
        public int DisposeCalls;
        public int ReadsDuringDisposal;
        public int Count
        {
            get
            {
                if (Volatile.Read(ref DisposeCalls) != 0) Interlocked.Increment(ref ReadsDuringDisposal);
                return inner.Count;
            }
        }
        public ReadOnlySpan<byte> this[int index] => inner[index];
        public void Dispose()
        {
            if (Interlocked.Increment(ref DisposeCalls) != 1) return;
            try { duringDisposal(); }
            finally { inner.Dispose(); }
        }
    }
}
