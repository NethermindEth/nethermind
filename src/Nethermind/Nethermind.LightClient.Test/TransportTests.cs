// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Autofac;
using DotNetty.Buffers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus.Scheduler;
using Nethermind.BeaconChain.Spec;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Config;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery;
using Nethermind.Network.Enr;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols;
using Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V70.Messages;
using Nethermind.Network.P2P.Subprotocols.Snap.V1.Messages;
using Nethermind.Network.P2P.Subprotocols.Snap.V2.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Forks;
using Nethermind.State.Proofs;
using Nethermind.State.Snap;
using Nethermind.State.SnapServer;
using Nethermind.Synchronization;
using Nethermind.Synchronization.Peers;
using Nethermind.Stats.Model;
using Nethermind.TxPool;
using NSubstitute;
using ISession = Nethermind.Network.P2P.ISession;
using Eth62Messages = Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Eth66Messages = Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;
using ReferenceCountUtil = DotNetty.Common.Utilities.ReferenceCountUtil;

namespace Nethermind.LightClient.Test;

public class TransportTests
{
    [Test]
    [CancelAfter(30_000)]
    public async Task Payload_downloads_are_shared_without_sharing_caller_cancellation([Values] bool cancelLeader, CancellationToken token)
    {
        await using VerifiedPayloadCache<int> cache = new(static _ => 1024, token);
        using CancellationTokenSource leader = CancellationTokenSource.CreateLinkedTokenSource(token);
        using CancellationTokenSource follower = CancellationTokenSource.CreateLinkedTokenSource(token);
        TaskCompletionSource<int> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken ownerToken = default;
        int fetched = 0;
        Task<int> Fetch(CancellationToken cancellation)
        {
            Interlocked.Increment(ref fetched);
            ownerToken = cancellation;
            return release.Task.WaitAsync(cancellation);
        }
        Hash256 hash = Keccak.Compute("shared verified payload");
        Task<int> first = cache.GetAsync(hash, Fetch, leader.Token);
        Task<int> second = cache.GetAsync(hash, Fetch, follower.Token);
        try
        {
            (cancelLeader ? leader : follower).Cancel();
            await Assert.CatchAsync<OperationCanceledException>(async () => await (cancelLeader ? first : second));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(fetched, Is.EqualTo(1));
                Assert.That(ownerToken.IsCancellationRequested, Is.False);
                Assert.That((cancelLeader ? second : first).IsCompleted, Is.False);
            }
            release.SetResult(42);
            Assert.That(await (cancelLeader ? second : first), Is.EqualTo(42));
            Assert.That(await cache.GetAsync(hash, Fetch, token), Is.EqualTo(42));
            Assert.That(fetched, Is.EqualTo(1));
        }
        finally { release.TrySetResult(42); }
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Payload_cache_does_not_retain_failed_downloads([Values] bool canceled, CancellationToken token)
    {
        await using VerifiedPayloadCache<int> cache = new(static _ => 1024, token);
        int fetched = 0;
        Task<int> Fetch(CancellationToken _) => ++fetched == 1
            ? canceled ? Task.FromCanceled<int>(new CancellationToken(true)) : Task.FromException<int>(new InvalidDataException("Invalid payload."))
            : Task.FromResult(42);
        Hash256 hash = Keccak.Compute("retry verified payload");

        if (canceled) await Assert.CatchAsync<OperationCanceledException>(async () => await cache.GetAsync(hash, Fetch, token));
        else await Assert.ThrowsAsync<InvalidDataException>(async () => await cache.GetAsync(hash, Fetch, token));

        Assert.That(await cache.GetAsync(hash, Fetch, token), Is.EqualTo(42));
        Assert.That(await cache.GetAsync(hash, Fetch, token), Is.EqualTo(42));
        Assert.That(fetched, Is.EqualTo(2));
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Payload_cache_returns_oversized_values_without_retaining_them(
        [Values(VerifiedPayloadCache<int>.Capacity, VerifiedPayloadCache<int>.Capacity + 1)] long bytes, CancellationToken token)
    {
        await using VerifiedPayloadCache<int> cache = new(_ => bytes, token);
        int fetched = 0;
        Task<int> Fetch(CancellationToken _) => Task.FromResult(++fetched);
        Hash256 hash = Keccak.Compute("bounded verified payload");

        Assert.That(await cache.GetAsync(hash, Fetch, token), Is.EqualTo(1));
        Assert.That(await cache.GetAsync(hash, Fetch, token), Is.EqualTo(bytes > VerifiedPayloadCache<int>.Capacity ? 2 : 1));
        Assert.That(fetched, Is.EqualTo(bytes > VerifiedPayloadCache<int>.Capacity ? 2 : 1));
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Payload_download_capacity_bounds_distinct_hashes_and_allows_shared_waiters([Values] bool cancelQueued, CancellationToken token)
    {
        await using VerifiedPayloadCache<int> cache = new(static _ => 1024, token);
        using CancellationTokenSource caller = CancellationTokenSource.CreateLinkedTokenSource(token);
        TaskCompletionSource<int> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int fetched = 0;
        Task<int> Fetch(CancellationToken cancellation)
        {
            Interlocked.Increment(ref fetched);
            return release.Task.WaitAsync(cancellation);
        }
        Task<int>[] active = Enumerable.Range(0, VerifiedPayloadCache<int>.MaxDownloads)
            .Select(i => cache.GetAsync(Keccak.Compute($"payload {i}"), Fetch, token)).ToArray();
        Task<int> queued = cache.GetAsync(Keccak.Compute("queued payload"), Fetch, caller.Token);
        Task<int> shared = cache.GetAsync(Keccak.Compute("payload 0"), Fetch, token);
        try
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(fetched, Is.EqualTo(VerifiedPayloadCache<int>.MaxDownloads));
                Assert.That(queued.IsCompleted, Is.False);
                Assert.That(shared.IsCompleted, Is.False);
            }
            if (cancelQueued)
            {
                caller.Cancel();
                await Assert.CatchAsync<OperationCanceledException>(async () => await queued);
            }
            release.SetResult(42);
            Assert.That(await Task.WhenAll(active), Is.All.EqualTo(42));
            Assert.That(await shared, Is.EqualTo(42));
            if (!cancelQueued) Assert.That(await queued, Is.EqualTo(42));
            Assert.That(fetched, Is.EqualTo(VerifiedPayloadCache<int>.MaxDownloads + (cancelQueued ? 0 : 1)));
        }
        finally { release.TrySetResult(42); }
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Payload_shutdown_cancels_waiters_and_joins_unwinding_owners([Values] bool externalStop, CancellationToken token)
    {
        using CancellationTokenSource stopping = CancellationTokenSource.CreateLinkedTokenSource(token);
        VerifiedPayloadCache<int> cache = new(static _ => 1024, stopping.Token);
        TaskCompletionSource<int> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<int> Fetch(CancellationToken cancellation)
        {
            using CancellationTokenRegistration registration = cancellation.Register(() => canceled.TrySetResult());
            return await release.Task;
        }
        Task<int>[] active = Enumerable.Range(0, VerifiedPayloadCache<int>.MaxDownloads)
            .Select(i => cache.GetAsync(Keccak.Compute($"stopping payload {i}"), Fetch, token)).ToArray();
        Task<int> queued = cache.GetAsync(Keccak.Compute("stopping queued payload"), Fetch, token);
        try
        {
            if (externalStop) await stopping.CancelAsync();
            Task shutdown = cache.DisposeAsync().AsTask();
            await canceled.Task.WaitAsync(token);
            Assert.That(shutdown.IsCompleted, Is.False, "owners retain their data until they finish unwinding");
            await Assert.CatchAsync<OperationCanceledException>(async () => await queued);
            foreach (Task<int> caller in active)
                await Assert.CatchAsync<OperationCanceledException>(async () => await caller);

            release.SetResult(42);
            await shutdown;
            await Assert.ThrowsAsync<ObjectDisposedException>(async () => await cache.GetAsync(Keccak.Compute("after disposal"), Fetch, token));
        }
        finally
        {
            release.TrySetResult(42);
            await cache.DisposeAsync();
            await Task.WhenAll(active.Cast<Task>().Append(queued)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Transport_payload_cache_merges_downloads_and_isolates_caller_cancellation(
        [Values(AllocationContexts.Bodies, AllocationContexts.Receipts)] AllocationContexts context, CancellationToken token)
    {
        await using ExecutionPeers peers = new(1);
        using CancellationTokenSource caller = CancellationTokenSource.CreateLinkedTokenSource(token);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken ownerToken = default;
        peers.Ordered[0].BeforeResponse = (_, cancellation) =>
        {
            ownerToken = cancellation;
            return release.Task.WaitAsync(cancellation);
        };
        Task first = peers.Request(context, caller.Token);
        await peers.Started.Reader.ReadAsync(token);
        Task second = peers.Request(context, token);
        try
        {
            caller.Cancel();
            await Assert.CatchAsync<OperationCanceledException>(async () => await first);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ownerToken.IsCancellationRequested, Is.False);
                Assert.That(peers.Counts(context), Is.EqualTo(new[] { 1 }));
            }
            release.SetResult();
            await second;
            await peers.Request(context, token);
            Assert.That(peers.Counts(context), Is.EqualTo(new[] { 1 }));
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(first, second).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Authenticated_header_downloads_share_work_without_sharing_caller_cancellation(
        [Values] bool cancelLeader, CancellationToken token)
    {
        await using ExecutionPeers peers = new(1);
        using CancellationTokenSource leader = CancellationTokenSource.CreateLinkedTokenSource(token);
        using CancellationTokenSource follower = CancellationTokenSource.CreateLinkedTokenSource(token);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken ownerToken = default;
        peers.Ordered[0].BeforeResponse = (_, cancellation) =>
        {
            ownerToken = cancellation;
            return release.Task.WaitAsync(cancellation);
        };
        VerifiedHead head = peers.Head;
        Task<BlockHeader> first = peers.Transport.GetHeaderAsync(head, leader.Token);
        await peers.Started.Reader.ReadAsync(token);
        Task<BlockHeader> second = peers.Transport.GetHeaderAsync(head, follower.Token);
        try
        {
            await (cancelLeader ? leader : follower).CancelAsync();
            Assert.That(async () => await (cancelLeader ? first : second), Throws.InstanceOf<OperationCanceledException>());
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ownerToken.IsCancellationRequested, Is.False);
                Assert.That((cancelLeader ? second : first).IsCompleted, Is.False);
                Assert.That(peers.Counts(AllocationContexts.Headers), Is.EqualTo(new[] { 1 }));
            }
            release.SetResult();
            Assert.That((await (cancelLeader ? second : first)).Hash, Is.EqualTo(head.BlockHash));
            Assert.That((await peers.Transport.GetHeaderAsync(head with { Slot = head.Slot + 1 }, token)).Hash, Is.EqualTo(head.BlockHash));
            Assert.That(peers.Counts(AllocationContexts.Headers), Is.EqualTo(new[] { 1 }));
        }
        finally
        {
            release.TrySetResult();
            await ((Task)Task.WhenAll(first, second)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Header_cache_rechecks_authenticated_metadata_and_does_not_cache_failed_fetches(
        [Values] bool cacheFirst, [Values] bool wrongNumber, CancellationToken token)
    {
        await using ExecutionPeers peers = new(1);
        VerifiedHead head = peers.Head;
        if (cacheFirst) await peers.Transport.GetHeaderAsync(head, token);
        VerifiedHead inconsistent = wrongNumber ? head with { Number = head.Number + 1 }
            : head with { StateRoot = Keccak.Compute("inconsistent authenticated state root") };

        Assert.That(async () => await peers.Transport.GetHeaderAsync(inconsistent, token),
            Throws.TypeOf<RpcException>().With.Property(nameof(RpcException.Code)).EqualTo(-32000));

        BlockHeader recovered = await peers.Transport.GetHeaderAsync(head, token);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(recovered.Hash, Is.EqualTo(head.BlockHash));
            Assert.That(recovered.Number, Is.EqualTo(head.Number));
            Assert.That(recovered.StateRoot, Is.EqualTo(head.StateRoot));
            Assert.That(peers.Counts(AllocationContexts.Headers), Is.EqualTo(new[] { cacheFirst ? 1 : 2 }));
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Cached_headers_do_not_share_peer_or_caller_fields(CancellationToken token)
    {
        await using ExecutionPeers peers = new(1);
        peers.Header.ExtraData = [1, 2];
        peers.Header.Bloom = new Bloom();
        peers.Header.Bloom.Bytes[0] = 1;
        peers.Header.Hash = peers.Header.CalculateHash();
        VerifiedHead head = peers.Head;
        ulong timestamp = peers.Header.Timestamp;
        byte[] bloom = peers.Header.Bloom.ReadOnlyBytes.ToArray();
        BlockHeader first = await peers.Transport.GetHeaderAsync(head, token);
        first.Number++;
        first.Timestamp++;
        first.StateRoot = Keccak.Zero;
        first.ExtraData[0] ^= 0xff;
        first.Bloom!.Bytes[0] ^= 0xff;
        peers.Header.Number++;
        peers.Header.Timestamp++;
        peers.Header.StateRoot = Keccak.Zero;
        peers.Header.ExtraData[1] ^= 0xff;
        peers.Header.Bloom.Bytes[1] ^= 0xff;

        BlockHeader second = await peers.Transport.GetHeaderAsync(head, token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(second, Is.Not.SameAs(first).And.Not.SameAs(peers.Header));
            Assert.That(second.Hash, Is.EqualTo(head.BlockHash));
            Assert.That(second.Number, Is.EqualTo(head.Number));
            Assert.That(second.Timestamp, Is.EqualTo(timestamp));
            Assert.That(second.StateRoot, Is.EqualTo(head.StateRoot));
            Assert.That(second.ExtraData, Is.EqualTo(new byte[] { 1, 2 }));
            Assert.That(second.ExtraData, Is.Not.SameAs(first.ExtraData).And.Not.SameAs(peers.Header.ExtraData));
            Assert.That(second.Bloom, Is.Not.SameAs(first.Bloom).And.Not.SameAs(peers.Header.Bloom));
            Assert.That(second.Bloom!.ReadOnlyBytes.ToArray(), Is.EqualTo(bloom));
            Assert.That(peers.Counts(AllocationContexts.Headers), Is.EqualTo(new[] { 1 }));
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Snap_state_downloads_are_shared_without_sharing_caller_cancellation(
        [Values(0, 1, 2)] int operation, [Values] bool cancelLeader, CancellationToken token)
    {
        await using ExecutionPeers peers = new(1, snap1: true);
        using CancellationTokenSource leader = CancellationTokenSource.CreateLinkedTokenSource(token);
        using CancellationTokenSource follower = CancellationTokenSource.CreateLinkedTokenSource(token);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken ownerToken = default;
        peers.Ordered[0].BeforeResponse = (_, cancellation) =>
        {
            ownerToken = cancellation;
            return release.Task.WaitAsync(cancellation);
        };
        Task first = peers.RequestSnap(operation, leader.Token);
        await peers.Started.Reader.ReadAsync(token);
        Task second = peers.RequestSnap(operation, follower.Token);
        try
        {
            (cancelLeader ? leader : follower).Cancel();
            await Assert.CatchAsync<OperationCanceledException>(async () => await (cancelLeader ? first : second));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ownerToken.IsCancellationRequested, Is.False);
                Assert.That((cancelLeader ? second : first).IsCompleted, Is.False);
                Assert.That(peers.Counts(AllocationContexts.Snap), Is.EqualTo(new[] { 1 }));
            }
            release.SetResult();
            await (cancelLeader ? second : first);
            await peers.RequestSnap(operation, token);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(peers.Counts(AllocationContexts.Snap), Is.EqualTo(new[] { 1 }));
                Assert.That(peers.Ordered[0].ResponsesDisposed, Is.EqualTo(1));
            }
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(first, second).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Cached_code_does_not_share_peer_or_caller_buffers(CancellationToken token)
    {
        await using ExecutionPeers peers = new(1, snap1: true);
        byte[] expected = peers.SnapCode.ToArray();
        byte[] first = await peers.Transport.GetCodeAsync(peers.SnapAccount, token);
        first[0] ^= 0xff;
        peers.SnapCode[1] ^= 0xff;

        byte[] second = await peers.Transport.GetCodeAsync(peers.SnapAccount, token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(second, Is.EqualTo(expected));
            Assert.That(second, Is.Not.SameAs(first));
            Assert.That(second, Is.Not.SameAs(peers.SnapCode));
            Assert.That(peers.Counts(AllocationContexts.Snap), Is.EqualTo(new[] { 1 }));
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task State_cache_values_are_isolated_by_authenticated_commitment([Values(0, 1, 2)] int operation, CancellationToken token)
    {
        await using ExecutionPeers peers = new(1, snap1: true);
        async Task<object> Read(VerifiedHead head, Account account) => operation switch
        {
            0 => await peers.Transport.GetAccountAsync(head, ExecutionPeers.SnapAddress, token),
            1 => await peers.Transport.GetStorageAsync(head, ExecutionPeers.SnapAddress, account, 1, token),
            2 => await peers.Transport.GetCodeAsync(account, token),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
        VerifiedHead previousHead = peers.SnapHead;
        Account previousAccount = peers.SnapAccount;
        object previous = await Read(previousHead, previousAccount);
        VerifiedHead nextHead = previousHead with { Slot = 3, Number = previousHead.Number + 1, BlockHash = Keccak.Compute("next verified header") };
        Assert.That(await Read(nextHead, previousAccount), Is.EqualTo(previous));
        Assert.That(peers.Counts(AllocationContexts.Snap), Is.EqualTo(new[] { 1 }), "unchanged commitments remain reusable at a new head");
        peers.SetSnapState(43, 124, [0x60, 0x02, 0x60, 0x00, 0x55]);

        object current = await Read(peers.SnapHead, peers.SnapAccount);
        object retained = await Read(previousHead, previousAccount);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(current, Is.Not.EqualTo(previous));
            Assert.That(retained, Is.EqualTo(previous));
            Assert.That(peers.Counts(AllocationContexts.Snap), Is.EqualTo(new[] { 2 }));
            Assert.That(peers.Ordered[0].ResponsesDisposed, Is.EqualTo(2));
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task State_cache_keys_include_the_account_address_and_storage_slot([Values] bool storage, CancellationToken token)
    {
        await using ExecutionPeers peers = new(1, snap1: true);
        if (storage)
        {
            Assert.That(await peers.Transport.GetStorageAsync(peers.SnapHead, ExecutionPeers.SnapAddress, peers.SnapAccount, 1, token), Is.EqualTo((UInt256)42));
            Assert.That(await peers.Transport.GetStorageAsync(peers.SnapHead, ExecutionPeers.SnapAddress, peers.SnapAccount, 2, token), Is.EqualTo(UInt256.Zero));
            Assert.That(await peers.Transport.GetStorageAsync(peers.SnapHead, ExecutionPeers.SnapAddress, peers.SnapAccount, 1, token), Is.EqualTo((UInt256)42));
        }
        else
        {
            Assert.That(await peers.Transport.GetAccountAsync(peers.SnapHead, ExecutionPeers.SnapAddress, token), Is.EqualTo(peers.SnapAccount));
            Assert.That(await peers.Transport.GetAccountAsync(peers.SnapHead, Address.Zero, token), Is.EqualTo(Account.TotallyEmpty));
            Assert.That(await peers.Transport.GetAccountAsync(peers.SnapHead, ExecutionPeers.SnapAddress, token), Is.EqualTo(peers.SnapAccount));
        }
        Assert.That(peers.Counts(AllocationContexts.Snap), Is.EqualTo(new[] { 2 }));
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Cached_receipts_do_not_share_mutable_rpc_metadata([Values] bool headerFirst, CancellationToken token)
    {
        await using ExecutionPeers peers = new(1);
        Block block = new(peers.Header, peers.Body);
        TxReceipt[] first = await peers.Transport.GetReceiptsAsync(block.Header, token, headerFirst ? null : block.Transactions.Length);
        first[0].BlockNumber = 123;
        first[0].BlockHash = Keccak.Compute("another block");
        first[0].TxHash = Keccak.Compute("another transaction");
        first[0].Index = 9;
        first[0].GasUsed = 77;

        TxReceipt[] second = await peers.Transport.GetReceiptsAsync(block.Header, token, headerFirst ? block.Transactions.Length : null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(second, Is.Not.SameAs(first));
            Assert.That(second[0], Is.Not.SameAs(first[0]));
            Assert.That(second[0].BlockNumber, Is.EqualTo(peers.Receipts[0].BlockNumber));
            Assert.That(second[0].BlockHash, Is.EqualTo(peers.Receipts[0].BlockHash));
            Assert.That(second[0].TxHash, Is.EqualTo(peers.Receipts[0].TxHash));
            Assert.That(second[0].Index, Is.EqualTo(peers.Receipts[0].Index));
            Assert.That(second[0].GasUsed, Is.EqualTo(peers.Receipts[0].GasUsed));
            Assert.That(peers.Counts(AllocationContexts.Receipts), Is.EqualTo(new[] { 1 }));
        }

        Assert.That(async () => await peers.Transport.GetReceiptsAsync(block.Header, token, block.Transactions.Length + 1),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("receipt count"));
        Assert.That(peers.Counts(AllocationContexts.Receipts), Is.EqualTo(new[] { 1 }), "a cached root proof does not replace a later exact-count check");
    }

    [TestCaseSource(nameof(ReceiptCountBounds))]
    public void Receipt_count_limit_uses_the_authenticated_header_and_owning_fork(IReleaseSpec spec, ulong gasLimit, int? expected)
    {
        BlockHeader header = EmptyHeader();
        header.GasLimit = gasLimit;
        Assert.That(ExecutionPeerTransport.GetReceiptCountLimit(header, spec), Is.EqualTo(expected));
    }

    private static IEnumerable<TestCaseData> ReceiptCountBounds()
    {
        yield return new TestCaseData(Frontier.Instance, 42_000UL, 4);
        yield return new TestCaseData(London.Instance, 42_000UL, 2);
        yield return new TestCaseData(Amsterdam.Instance, 48_000UL, 4);
        yield return new TestCaseData(Eip8141Prototype.Instance, 48_000UL, 4);
        yield return new TestCaseData(new ReleaseSpec { IsEip2780Enabled = true, IsEip3529Enabled = true }, 48_000UL, 5);
        yield return new TestCaseData(new ReleaseSpec { IsEip8141Enabled = true, IsEip3529Enabled = true }, 48_000UL, 5);
        yield return new TestCaseData(new ReleaseSpec { IsEip3298Enabled = true }, 42_000UL, null);
        yield return new TestCaseData(new ReleaseSpec { IsEip3298Enabled = true, IsEip7778Enabled = true }, 42_000UL, 2);
        yield return new TestCaseData(Frontier.Instance, 10_499UL, 0);
        yield return new TestCaseData(Frontier.Instance, 10_500UL, 1);
        yield return new TestCaseData(Frontier.Instance, ulong.MaxValue, BlockBodyDecoder.TransactionsCountLimit.Limit);
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Header_only_receipts_separate_decode_limits_from_unknown_transaction_counts(CancellationToken token)
    {
        await using ExecutionPeers peers = new(1);
        TxReceipt[] receipts = await peers.Transport.GetReceiptsAsync(peers.Header, token);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(receipts, Has.Length.EqualTo(peers.Receipts.Length));
        Assert.That(peers.Ordered[0].LastReceiptCounts.IsEmpty, Is.True);
        Assert.That(peers.Ordered[0].LastReceiptLimits?.ToArray(), Is.EqualTo(new[]
        {
            ExecutionPeerTransport.GetReceiptCountLimit(peers.Header, peers.Transport.SpecProvider.GetSpec(peers.Header))!.Value,
        }));
        Assert.That(peers.Counts(AllocationContexts.Bodies), Is.EqualTo(new[] { 0 }));
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task An_incomplete_receipt_tail_is_retried_before_root_authenticated_results_are_returned(CancellationToken token)
    {
        await using ExecutionPeers peers = new(2);
        peers.Ordered[0].MissingReceipts = true;

        TxReceipt[] receipts = await peers.Transport.GetReceiptsAsync(peers.Header, token);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(receipts, Has.Length.EqualTo(peers.Receipts.Length));
        Assert.That(peers.Counts(AllocationContexts.Receipts), Is.EqualTo(new[] { 1, 1 }));
        Assert.That(peers.Counts(AllocationContexts.Bodies), Is.EqualTo(new[] { 0, 0 }));
        Assert.That(peers.Ordered[0].ResponsesDisposed, Is.EqualTo(1));
    }

    [Test]
    [NonParallelizable]
    [CancelAfter(30_000)]
    public async Task Receipt_requests_use_authenticated_context_for_eth70_paging(
        [Values] bool paged, [Values] bool exactCount, CancellationToken token)
    {
        await using ReceiptPeers peers = new(paged);
        BlockHeader header = peers.AddBlock(100);

        TxReceipt[] receipts = await peers.Transport.GetReceiptsAsync(header, token, exactCount ? 2 : null);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(receipts.Select(receipt => receipt.GasUsedTotal), Is.EqualTo(new ulong[] { 21_000, 42_000 }));
        Assert.That(receipts.Select(receipt => receipt.Logs![0].Data[0]), Is.EqualTo(new byte[] { 1, 2 }));
        Assert.That(peers.Primary.Requests.Select(request => request.Offset), Is.EqualTo(paged ? new long[] { 0, 1 } : new long[] { 0 }));
        Assert.That(peers.Fallback.Requests, Is.Empty, "a complete fallback makes missing paging context fail without a timeout");
        Assert.That(peers.Server.FindHeader(header.Hash!), Is.Null);
        Assert.That(peers.Sessions.Sum(peer => peer.BodyRequests), Is.Zero);
        Assert.That(peers.Sessions.All(peer => peer.BuffersReleased), Is.True);
    }

    [Test]
    [NonParallelizable]
    [CancelAfter(30_000)]
    public async Task Paged_receipts_reject_invalid_data_and_retry_without_retaining_context(
        [Values("root", "count", "size")] string invalid, CancellationToken token)
    {
        await using ReceiptPeers peers = new(true);
        BlockHeader header = peers.AddBlock(100);
        TxReceipt[] valid = peers.Blocks[header.Hash!].Receipts;
        peers.Primary.Respond = request =>
        {
            if (invalid == "root")
            {
                TxReceipt[] forged = [new(valid[0]) { Logs = [new LogEntry(Address.Zero, [0xff], [])] }, valid[1]];
                peers.Reply(request, forged);
            }
            else
            {
                TxReceipt[] oversized = invalid == "count"
                    ? Enumerable.Repeat(valid[0], ExecutionPeerTransport.GetReceiptCountLimit(header, peers.Transport.SpecProvider.GetSpec(header))!.Value + 1).ToArray()
                    : [new(valid[0]) { Logs = [new LogEntry(Address.Zero, new byte[checked((int)(header.GasLimit / 8 + 1))], [])] }];
                try { request.Peer.Reply(request, oversized, invalid == "size"); }
                catch (SubprotocolException exception)
                {
                    request.Peer.WireErrors.Enqueue(exception);
                    request.Peer.Reply(request, [], false);
                }
            }
        };

        TxReceipt[] receipts = await peers.Transport.GetReceiptsAsync(header, token);
        TxReceipt[] cached = await peers.Transport.GetReceiptsAsync(header, token, 2);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(receipts.Select(receipt => receipt.Logs![0].Data[0]), Is.EqualTo(new byte[] { 1, 2 }));
        Assert.That(cached.Select(receipt => receipt.Logs![0].Data[0]), Is.EqualTo(new byte[] { 1, 2 }));
        Assert.That(peers.Fallback.Requests, Has.Count.EqualTo(1));
        Assert.That(peers.Primary.Requests, Has.Count.EqualTo(invalid == "root" ? 2 : 1));
        Assert.That(peers.Primary.WireErrors.Select(error => error.Message),
            invalid == "count" ? Has.Some.Contains("exceeds the request") : Is.Empty);
        Assert.That(peers.Server.FindHeader(header.Hash!), Is.Null);
        Assert.That(peers.Sessions.All(peer => peer.BuffersReleased), Is.True);
    }

    [Test]
    [NonParallelizable]
    [CancelAfter(30_000)]
    public async Task Canceling_a_receipt_waiter_preserves_the_shared_paging_owner(CancellationToken token)
    {
        await using ReceiptPeers peers = new(true);
        peers.Primary.Respond = null;
        peers.Fallback.SyncPeer.IsInitialized = false;
        BlockHeader header = peers.AddBlock(100);
        using CancellationTokenSource caller = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<TxReceipt[]> canceled = peers.Transport.GetReceiptsAsync(header, caller.Token);
        ReceiptRequest first = await peers.Requests.Reader.ReadAsync(token);
        Task<TxReceipt[]> survivor = peers.Transport.GetReceiptsAsync(header, token, 2);
        try
        {
            await caller.CancelAsync();
            Assert.That(async () => await canceled, Throws.InstanceOf<OperationCanceledException>());
            Assert.That(peers.Server.FindHeader(header.Hash!), Is.Not.Null);
            peers.Reply(first);
            ReceiptRequest continuation = await peers.Requests.Reader.ReadAsync(token);
            peers.Reply(continuation);
            Assert.That(await survivor.WaitAsync(token), Has.Length.EqualTo(2));
            using IDisposable assertionScope = Assert.EnterMultipleScope();
            Assert.That(peers.Primary.Requests, Has.Count.EqualTo(2));
            Assert.That(peers.Fallback.Requests, Is.Empty);
            Assert.That(peers.Server.FindHeader(header.Hash!), Is.Null);
        }
        finally
        {
            await peers.DisposeAsync();
            await ((Task)Task.WhenAll(canceled, survivor)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Test]
    [NonParallelizable]
    [CancelAfter(30_000)]
    public async Task Receipt_contexts_are_bounded_by_admitted_bulk_requests(CancellationToken token)
    {
        await using ReceiptPeers peers = new(false);
        foreach (ReceiptPeer peer in peers.Sessions) peer.Respond = null;
        BlockHeader[] headers = Enumerable.Range(100, 4).Select(number => peers.AddBlock((ulong)number)).ToArray();
        Task<TxReceipt[]>[] requests = headers.Select(header => peers.Transport.GetReceiptsAsync(header, token)).ToArray();
        try
        {
            ReceiptRequest[] active = new ReceiptRequest[6];
            for (int i = 0; i < active.Length; i++) active[i] = await peers.Requests.Reader.ReadAsync(token);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(active.Select(request => request.Hash).Distinct().Count(), Is.EqualTo(3));
                Assert.That(active.GroupBy(request => request.Hash).All(group => group.Count() == 2), Is.True);
                Assert.That(headers.Count(header => peers.Server.FindHeader(header.Hash!) is not null), Is.EqualTo(3));
                Assert.That(peers.Requests.Reader.TryRead(out _), Is.False);
            }
            Assert.That((await peers.Transport.GetHeaderByHashAsync(headers[3].Hash!, token)).Hash, Is.EqualTo(headers[3].Hash));
            Hash256 completed = active[0].Hash;
            foreach (ReceiptRequest request in active.Where(request => request.Hash == completed)) peers.Reply(request);
            await requests[Array.FindIndex(headers, header => header.Hash == completed)].WaitAsync(token);
            ReceiptRequest[] fourth = new ReceiptRequest[2];
            for (int i = 0; i < fourth.Length; i++) fourth[i] = await peers.Requests.Reader.ReadAsync(token);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(fourth.All(request => request.Hash == headers[3].Hash), Is.True);
                Assert.That(peers.Server.FindHeader(completed), Is.Null);
                Assert.That(headers.Count(header => peers.Server.FindHeader(header.Hash!) is not null), Is.EqualTo(3));
                Assert.That(peers.Requests.Reader.TryRead(out _), Is.False);
            }
            foreach (ReceiptRequest request in active.Where(request => request.Hash != completed).Concat(fourth)) peers.Reply(request);
            await Task.WhenAll(requests).WaitAsync(token);
            using IDisposable assertionScope = Assert.EnterMultipleScope();
            Assert.That(headers.All(header => peers.Server.FindHeader(header.Hash!) is null), Is.True);
            Assert.That(peers.Sessions.Sum(peer => peer.BodyRequests), Is.Zero);
        }
        finally
        {
            await peers.DisposeAsync();
            await ((Task)Task.WhenAll(requests)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Test]
    [NonParallelizable]
    [CancelAfter(30_000)]
    public async Task Stopping_paged_receipts_joins_cleanup_before_disposing_services(CancellationToken token)
    {
        await using ReceiptPeers peers = new(true);
        peers.Primary.Respond = null;
        peers.Fallback.SyncPeer.IsInitialized = false;
        BlockHeader header = peers.AddBlock(100);
        Task<TxReceipt[]> request = peers.Transport.GetReceiptsAsync(header, token);
        try
        {
            peers.Reply(await peers.Requests.Reader.ReadAsync(token));
            ReceiptRequest continuation = await peers.Requests.Reader.ReadAsync(token);
            Assert.That(continuation.Offset, Is.EqualTo(1));
            Assert.That(peers.Server.FindHeader(header.Hash!), Is.Not.Null);
            await peers.DisposeAsync();
            Assert.That(async () => await request, Throws.InstanceOf<OperationCanceledException>());
            using IDisposable assertionScope = Assert.EnterMultipleScope();
            Assert.That(peers.Server.FindHeader(header.Hash!), Is.Null);
            Assert.That(peers.Sessions.All(peer => peer.BuffersReleased), Is.True);
        }
        finally
        {
            await peers.DisposeAsync();
            await ((Task)request).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Test]
    [NonParallelizable]
    [CancelAfter(30_000)]
    public async Task Receipt_context_detaches_header_fields_and_preserves_server_contract(CancellationToken token)
    {
        await using ReceiptPeers peers = new(false);
        peers.Primary.Respond = null;
        BlockHeader header = peers.AddBlock(100);
        Task<TxReceipt[]> request = peers.Transport.GetReceiptsAsync(header, token);
        ReceiptRequest wire = await peers.Requests.Reader.ReadAsync(token);
        BlockHeader owned = peers.Server.FindHeader(header.Hash!)!;
        Assert.That(owned, Is.Not.Null);
        try
        {
            ulong gasLimit = owned.GasLimit;
            ulong timestamp = owned.Timestamp;
            header.GasLimit = 0;
            header.GasUsed = 0;
            header.Timestamp = ulong.MaxValue;
            using IOwnedReadOnlyList<BlockHeader> unavailable = peers.Server.FindHeaders(header.Hash!, 1, 0, false);
            Block? genesis = peers.Server.Find(peers.Server.Genesis.Hash!);
            Assert.That(genesis, Is.Not.Null);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(owned, Is.Not.SameAs(header));
                Assert.That(owned.GasLimit, Is.EqualTo(gasLimit));
                Assert.That(owned.Timestamp, Is.EqualTo(timestamp));
                Assert.That(peers.Server.FindHeader(peers.Server.Genesis.Hash!), Is.SameAs(peers.Server.Genesis));
                Assert.That(genesis!.Header, Is.SameAs(peers.Server.Genesis));
                Assert.That(peers.Server.FindHash(0), Is.EqualTo(peers.Server.Genesis.Hash));
                Assert.That(peers.Server.Find(header.Hash!), Is.Null);
                Assert.That(unavailable, Is.Empty);
                Assert.That(peers.Server.FindHash(header.Number), Is.Null);
                Assert.That(peers.Server.GetReceipts(header.Hash!), Is.Null);
            }
            peers.Reply(wire);
            Assert.That(await request.WaitAsync(token), Has.Length.EqualTo(2));
            Assert.That(peers.Server.FindHeader(header.Hash!), Is.Null);
            BlockHeader replacement = owned.Clone();
            ExecutionPeerTransport.LightSyncServer server = (ExecutionPeerTransport.LightSyncServer)peers.Server;
            server.AddReceiptHeader(replacement);
            try
            {
                server.RemoveReceiptHeader(owned);
                Assert.That(server.FindHeader(header.Hash!), Is.SameAs(replacement), "an old owner cannot remove a later registration");
            }
            finally { server.RemoveReceiptHeader(replacement); }
        }
        finally
        {
            await peers.DisposeAsync();
            await ((Task)request).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Payload_prefetch_reserves_a_request_slot_for_authenticated_headers([Values] bool mixed, CancellationToken token)
    {
        await using ExecutionPeers peers = new(1);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        peers.Ordered[0].BeforeResponse = (context, cancellation) => context == AllocationContexts.Headers
            ? Task.CompletedTask : release.Task.WaitAsync(cancellation);
        Task[] active = await StartBulkRequestsAsync(peers, mixed, token);
        Task queued = peers.RequestPayload(AllocationContexts.Bodies, 4, token);
        try
        {
            Task header = peers.Request(AllocationContexts.Headers, token);
            Assert.That(header.IsCompleted, Is.True, "a responsive header peer must not queue behind the four slow payloads");
            await header;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(peers.BulkRequests, Is.EqualTo(3));
                Assert.That(peers.Counts(AllocationContexts.Headers), Is.EqualTo(new[] { 1 }));
                Assert.That(queued.IsCompleted, Is.False);
                Assert.That(active.All(task => !task.IsCompleted), Is.True);
            }
            release.SetResult();
            await Task.WhenAll(active.Append(queued));
            Assert.That(peers.BulkRequests, Is.EqualTo(4));
        }
        finally { release.TrySetResult(); }
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Queued_payload_caller_cancellation_preserves_header_capacity_and_releases_budget(CancellationToken token)
    {
        await using ExecutionPeers peers = new(1);
        using CancellationTokenSource caller = CancellationTokenSource.CreateLinkedTokenSource(token);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        peers.Ordered[0].BeforeResponse = (context, cancellation) => context == AllocationContexts.Headers
            ? Task.CompletedTask : release.Task.WaitAsync(cancellation);
        Task[] active = await StartBulkRequestsAsync(peers, mixed: true, token);
        Task queued = peers.RequestPayload(AllocationContexts.Bodies, 4, caller.Token);
        try
        {
            caller.Cancel();
            await Assert.CatchAsync<OperationCanceledException>(async () => await queued);
            await peers.Request(AllocationContexts.Headers, token);
            Assert.That(peers.BulkRequests, Is.EqualTo(3));

            release.SetResult();
            await Task.WhenAll(active);
            await peers.RequestPayload(AllocationContexts.Bodies, 4, token);
            await peers.RequestPayload(AllocationContexts.Bodies, 5, token);
            Assert.That(peers.BulkRequests, Is.EqualTo(5));
        }
        finally { release.TrySetResult(); }
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Queued_payload_deadline_does_not_leak_bulk_request_budget(CancellationToken token)
    {
        await using ExecutionPeers peers = new(1);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        peers.Ordered[0].BeforeResponse = (context, _) => context == AllocationContexts.Headers
            ? Task.CompletedTask : release.Task;
        Task[] active = [];
        Task? queued = null;
        try
        {
            active = await StartBulkRequestsAsync(peers, mixed: false, token);
            queued = peers.RequestPayload(AllocationContexts.Bodies, 4, token);
            peers.Clock.Advance(TimeSpan.FromSeconds(30));
            RpcException error = (await Assert.ThrowsAsync<RpcException>(async () => await queued))!;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(error.Code, Is.EqualTo(-32000));
                Assert.That(peers.BulkRequests, Is.EqualTo(3));
                Assert.That(active.All(task => !task.IsCompleted), Is.True);
            }
            await peers.Request(AllocationContexts.Headers, token);

            release.SetResult();
            await Task.WhenAll(active).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await peers.RequestPayload(AllocationContexts.Bodies, 4, token);
            Assert.That(peers.BulkRequests, Is.EqualTo(4));
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(queued is null ? active : active.Append(queued)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Bulk_reservation_shutdown_cancels_queued_payloads_and_joins_active_owners(CancellationToken token)
    {
        await using ExecutionPeers peers = new(1);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        peers.Ordered[0].BeforeResponse = async (_, cancellation) =>
        {
            using CancellationTokenRegistration registration = cancellation.Register(() => canceled.TrySetResult());
            await release.Task;
        };
        Task[] active = [];
        Task? queued = null;
        Task? shutdown = null;
        try
        {
            active = await StartBulkRequestsAsync(peers, mixed: false, token);
            queued = peers.RequestPayload(AllocationContexts.Bodies, 4, token);
            shutdown = peers.DisposeAsync().AsTask();
            await canceled.Task.WaitAsync(token);
            Assert.That(shutdown.IsCompleted, Is.False, "the three bulk owners must release their reservations before disposal completes");
            await Assert.CatchAsync<OperationCanceledException>(async () => await queued);
            foreach (Task request in active)
                await Assert.CatchAsync<OperationCanceledException>(async () => await request);

            release.SetResult();
            await shutdown;
            Assert.That(peers.BulkRequests, Is.EqualTo(3));
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(queued is null ? active : active.Append(queued)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (shutdown is not null) await shutdown;
        }
    }

    private static async Task<Task[]> StartBulkRequestsAsync(ExecutionPeers peers, bool mixed, CancellationToken token)
    {
        Task[] requests = new Task[3];
        for (int i = 0; i < requests.Length; i++)
        {
            AllocationContexts context = mixed ? i switch
            {
                0 => AllocationContexts.Bodies,
                1 => AllocationContexts.Receipts,
                _ => AllocationContexts.BlockAccessLists,
            } : AllocationContexts.Bodies;
            requests[i] = context == AllocationContexts.BlockAccessLists
                ? peers.Request(context, token) : peers.RequestPayload(context, i + 1, token);
            await peers.Started.Reader.ReadAsync(token);
        }
        return requests;
    }

    [TestCase(29, 7, false, -1)]
    [TestCase(30, 8, false, -1)]
    [TestCase(30, 7, false, 7)]
    [TestCase(30, 7, true, 8)]
    [CancelAfter(30_000)]
    public async Task Snap_capacity_reserves_connections_only_below_its_available_threshold(
        int peerCount, int availableSnap, bool firstCandidateIsPriority, int victim, CancellationToken token)
    {
        await using ExecutionPeers peers = new(peerCount, snap1: true);
        if (firstCandidateIsPriority) peers.Ordered[availableSnap].SyncPeer.IsPriority = true;

        int connected = await RunSnapCapacityTickAsync(peers, availableSnap, token);

        Assert.That(connected, Is.EqualTo(availableSnap));
        AssertSnapCapacityVictim(peers, victim);
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Snap_capacity_uses_the_same_cooldown_expiry_as_requests(
        [Values] bool invalid, [Values] bool expired, CancellationToken token)
    {
        await using ExecutionPeers peers = new(30, snap1: true);
        peers.Ordered[0].InvalidData = invalid;
        if (!invalid) peers.Ordered[0].BeforeResponse = static (_, _) => Task.FromException(new IOException("Selected state unavailable."));
        await peers.Request(AllocationContexts.Snap, token);
        peers.Ordered[0].InvalidData = false;
        peers.Ordered[0].BeforeResponse = null;
        int expirySeconds = invalid ? 60 : 15;
        peers.Clock.Advance(TimeSpan.FromSeconds(expirySeconds - (expired ? 0 : 1) - 10));

        int connected = await RunSnapCapacityTickAsync(peers, 8, token);

        Assert.That(connected, Is.EqualTo(8), "cooldown changes availability, not connected SNAP capability");
        AssertSnapCapacityVictim(peers, expired ? -1 : 0);
    }

    private static async Task<int> RunSnapCapacityTickAsync(ExecutionPeers peers, int snapPeers, CancellationToken token)
    {
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool observing = false;
        ISnapSyncPeer snap = Substitute.For<ISnapSyncPeer>();
        for (int i = 0; i < peers.Ordered.Length; i++)
        {
            bool supported = i < snapPeers;
            peers.Ordered[i].SyncPeer.TryGetSatelliteProtocol<ISnapSyncPeer>(Protocol.Snap, out Arg.Any<ISnapSyncPeer>()).Returns(call =>
            {
                call[1] = supported ? snap : null;
                if (Volatile.Read(ref observing)) entered.TrySetResult();
                return supported;
            });
        }
        Assert.That(peers.Transport.SnapPeerCount, Is.EqualTo(snapPeers));
        Task worker = (Task)typeof(ExecutionPeerTransport)
            .GetMethod("MaintainSnapCapacityAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(peers.Transport, null)!;
        typeof(ExecutionPeerTransport).GetField("_snapCapacityTask", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(peers.Transport, worker);
        try
        {
            Assert.That(peers.Clock.ActiveTimerCount, Is.EqualTo(1), "the capacity worker must use the injected clock");
            Volatile.Write(ref observing, true);
            peers.Clock.Advance(TimeSpan.FromSeconds(9));
            Assert.That(entered.Task.IsCompleted, Is.False, "capacity is checked on the ten-second timer tick");
            peers.Clock.Advance(TimeSpan.FromSeconds(1));
            await entered.Task.WaitAsync(token);
            return peers.Transport.SnapPeerCount;
        }
        finally { await peers.DisposeAsync(); }
    }

    private static void AssertSnapCapacityVictim(ExecutionPeers peers, int victim)
    {
        for (int i = 0; i < peers.Ordered.Length; i++)
            peers.Ordered[i].SyncPeer.Received(i == victim ? 1 : 0).Disconnect(DisconnectReason.DropWorstPeer,
                "Reserving light-client connection capacity for SNAP peers");
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Snap_state_routes_authenticate_before_preferring_the_peer(
        [Values(0, 1, 2, 3)] int operation, [Values] bool snap2, CancellationToken token)
    {
        await using ExecutionPeers peers = new(2, snap1: !snap2, snap2: snap2);
        peers.Ordered[0].InvalidData = true;

        await peers.RequestSnap(operation, token);
        await peers.RequestSnap(operation, token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(peers.Counts(AllocationContexts.Snap), Is.EqualTo(new[] { 1, operation == 3 ? 4 : 1 }));
            Assert.That(peers.Ordered[0].ResponsesDisposed, Is.EqualTo(1));
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Snap_cooldown_policy_expires_without_wall_time_or_changing_connected_count([Values] bool invalid, CancellationToken token)
    {
        await using ExecutionPeers peers = new(2, snap1: true);
        peers.Ordered[0].InvalidData = invalid;
        if (!invalid) peers.Ordered[0].BeforeResponse = static (_, _) => Task.FromException(new IOException("Selected state unavailable."));
        await peers.Request(AllocationContexts.Snap, token);
        peers.Pool.RemovePeer(peers.Ordered[1].SyncPeer);
        peers.Ordered[0].InvalidData = false;
        peers.Ordered[0].BeforeResponse = null;
        peers.Clock.Advance(TimeSpan.FromSeconds(invalid ? 59 : 14));
        Task recovered = peers.Request(AllocationContexts.Snap, token);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(recovered.IsCompleted, Is.False);
            Assert.That(peers.Counts(AllocationContexts.Snap), Is.EqualTo(new[] { 1, 1 }));
            Assert.That(peers.Transport.SnapPeerCount, Is.EqualTo(1), "the status metric counts connected SNAP capability, including a cooling peer");
        }

        peers.Clock.Advance(TimeSpan.FromSeconds(1));
        await recovered;

        Assert.That(peers.Counts(AllocationContexts.Snap), Is.EqualTo(new[] { 2, 1 }));
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Snap_absence_remains_unavailable_while_BAL_absence_is_unsupported(CancellationToken token)
    {
        await using ExecutionPeers peers = new(1, ethVersion: 69);
        Task request = peers.Request(AllocationContexts.Snap, token);
        peers.Clock.Advance(TimeSpan.FromSeconds(30));
        RpcException unavailable = (await Assert.ThrowsAsync<RpcException>(async () => await request))!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(unavailable.Code, Is.EqualTo(-32000));
            Assert.That(unavailable.Message, Is.EqualTo("No execution peer supplied a valid SNAP proof for the selected verified state."));
            Assert.That(peers.Transport.SnapPeerCount, Is.Zero);
            Assert.That(peers.Counts(AllocationContexts.Snap), Is.EqualTo(new[] { 0 }));
        }
        Assert.That(async () => await peers.Request(AllocationContexts.BlockAccessLists, token), Throws.TypeOf<NotSupportedException>());
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Execution_requests_reuse_an_authenticated_peer_after_a_failed_prefix(
        [Values(AllocationContexts.Headers, AllocationContexts.Bodies, AllocationContexts.Receipts, AllocationContexts.BlockAccessLists, AllocationContexts.Snap)] AllocationContexts context,
        CancellationToken token)
    {
        await using ExecutionPeers peers = new(3, snap1: context == AllocationContexts.Snap);
        peers.Ordered[0].BeforeResponse = (_, _) => Task.FromException(new IOException("Unavailable data."));
        peers.Ordered[1].BeforeResponse = (_, _) => Task.FromException(new IOException("Unavailable data."));

        await peers.Request(context, token);
        await peers.Request(context, token);

        if (context is AllocationContexts.Bodies or AllocationContexts.Receipts)
        {
            Assert.That(peers.Counts(context), Is.EqualTo(new[] { 1, 1, 1 }));
            await peers.RequestPayload(context, 1, token);
        }
        Assert.That(peers.Counts(context), Is.EqualTo(new[] { 1, 1, 2 }));
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Execution_hedges_stalled_peers_without_cooling_the_canceled_loser(
        [Values(AllocationContexts.Headers, AllocationContexts.Bodies, AllocationContexts.Receipts, AllocationContexts.BlockAccessLists, AllocationContexts.Snap)] AllocationContexts context,
        CancellationToken token)
    {
        await using ExecutionPeers peers = new(2, snap1: context == AllocationContexts.Snap);
        TaskCompletionSource canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        peers.Ordered[0].BeforeResponse = async (_, cancellation) =>
        {
            using CancellationTokenRegistration registration = cancellation.Register(() => canceled.TrySetResult());
            await Task.Delay(Timeout.Infinite, cancellation);
        };

        await peers.Request(context, token);
        await canceled.Task.WaitAsync(token);
        Assert.That(peers.Counts(context), Is.EqualTo(new[] { 1, 1 }));
        Assert.That(peers.Clock.GetTimestamp(), Is.Zero, "a healthy hedge must complete before the stalled peer times out");

        peers.Pool.RemovePeer(peers.Ordered[1].SyncPeer);
        peers.Ordered[0].BeforeResponse = null;
        if (context is AllocationContexts.Bodies or AllocationContexts.Receipts)
            await peers.RequestPayload(context, 1, token);
        else await peers.Request(context, token);

        Assert.That(peers.Counts(context), Is.EqualTo(new[] { 2, 1 }), "canceling the losing attempt must not cool a healthy peer");
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Execution_hedges_at_most_two_peers_per_logical_request(
        [Values(AllocationContexts.Headers, AllocationContexts.Bodies, AllocationContexts.Receipts, AllocationContexts.BlockAccessLists, AllocationContexts.Snap)] AllocationContexts context,
        CancellationToken token)
    {
        await using ExecutionPeers peers = new(3, snap1: context == AllocationContexts.Snap);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        foreach (ExecutionPeer peer in peers.Ordered)
            peer.BeforeResponse = (_, cancellation) => release.Task.WaitAsync(cancellation);
        Task request = peers.Request(context, token);
        try
        {
            await peers.Started.Reader.ReadAsync(token);
            await peers.Started.Reader.ReadAsync(token);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(request.IsCompleted, Is.False);
                Assert.That(peers.Counts(context), Is.EqualTo(new[] { 1, 1, 0 }));
            }
            release.SetResult();
            await request;
            Assert.That(peers.Counts(context), Is.EqualTo(new[] { 1, 1, 0 }));
        }
        finally
        {
            release.TrySetResult();
            await request.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Execution_hedged_success_joins_and_disposes_a_late_raw_response(
        [Values(AllocationContexts.Bodies, AllocationContexts.Receipts, AllocationContexts.BlockAccessLists, AllocationContexts.Snap)] AllocationContexts context,
        CancellationToken token)
    {
        await using ExecutionPeers peers = new(2, snap1: context == AllocationContexts.Snap);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        peers.Ordered[0].BeforeResponse = async (_, cancellation) =>
        {
            using CancellationTokenRegistration registration = cancellation.Register(() => canceled.TrySetResult());
            await release.Task;
        };
        Task request = peers.Request(context, token);
        try
        {
            await canceled.Task.WaitAsync(token);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(request.IsCompleted, Is.False, "the request owns its losing raw response until that attempt unwinds");
                Assert.That(peers.Ordered[0].ResponsesDisposed, Is.Zero);
                Assert.That(peers.Ordered[1].ResponsesDisposed, Is.EqualTo(1));
                if (context == AllocationContexts.Receipts)
                    Assert.That(Services(peers.Transport).Resolve<ISyncServer>().FindHeader(peers.Header.Hash!), Is.Not.Null,
                        "receipt paging context remains available until both attempts finish");
            }
            release.SetResult();
            await request;
            Assert.That(peers.Ordered.Select(peer => peer.ResponsesDisposed), Is.All.EqualTo(1));
            Assert.That(peers.Counts(context), Is.EqualTo(new[] { 1, 1 }));
            if (context == AllocationContexts.Receipts)
                Assert.That(Services(peers.Transport).Resolve<ISyncServer>().FindHeader(peers.Header.Hash!), Is.Null);
        }
        finally
        {
            release.TrySetResult();
            await request.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Execution_late_success_retains_an_eligible_preferred_peer(
        [Values(AllocationContexts.Headers, AllocationContexts.Bodies, AllocationContexts.Receipts, AllocationContexts.BlockAccessLists, AllocationContexts.Snap)] AllocationContexts context,
        [Values] bool uninitialized, CancellationToken token)
    {
        await using ExecutionPeers peers = new(2, snap1: context == AllocationContexts.Snap);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        peers.Ordered[0].BeforeResponse = (_, cancellation) => release.Task.WaitAsync(cancellation);
        peers.Ordered[1].SyncPeer.IsInitialized = false;
        Task Request(int identity) => context is AllocationContexts.Bodies or AllocationContexts.Receipts
            ? peers.RequestPayload(context, identity, token) : peers.Request(context, token);
        Task earlier = Request(1);
        try
        {
            Assert.That(await peers.Started.Reader.ReadAsync(token), Is.SameAs(peers.Ordered[0]));
            peers.Ordered[1].SyncPeer.IsInitialized = true;
            await Request(2);
            Assert.That(await peers.Started.Reader.ReadAsync(token), Is.SameAs(peers.Ordered[1]));
            if (uninitialized) peers.Ordered[1].SyncPeer.IsInitialized = false;
            release.SetResult();
            await earlier;
            if (uninitialized) peers.Ordered[1].SyncPeer.IsInitialized = true;

            await Request(3);

            Assert.That(peers.Counts(context), Is.EqualTo(uninitialized ? new[] { 2, 1 } : new[] { 1, 2 }));
        }
        finally
        {
            release.TrySetResult();
            await earlier.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Execution_requests_reach_a_healthy_peer_across_overall_deadlines(
        [Values(3, 7, 15)] int slowPeers, [Values(AllocationContexts.Headers, AllocationContexts.Snap)] AllocationContexts context, CancellationToken token)
    {
        await using ExecutionPeers peers = new(slowPeers + 1, snap1: context == AllocationContexts.Snap);
        foreach (ExecutionPeer peer in peers.Ordered.Take(slowPeers))
            peer.BeforeResponse = static (_, cancellation) => Task.Delay(Timeout.Infinite, cancellation);
        bool supplied = false;
        for (int budget = 0; budget <= slowPeers / 3 && !supplied; budget++)
        {
            using CancellationTokenSource caller = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task request = peers.Request(context, caller.Token);
            try
            {
                for (int attempt = 0; attempt < 3 && !request.IsCompleted; attempt++)
                {
                    bool reachedHealthyPeer = false;
                    for (int hedge = 0; hedge < 2; hedge++)
                    {
                        using CancellationTokenSource reading = CancellationTokenSource.CreateLinkedTokenSource(caller.Token);
                        Task<ExecutionPeer> started = peers.Started.Reader.ReadAsync(reading.Token).AsTask();
                        if (await Task.WhenAny(request, started) == request)
                        {
                            reading.Cancel();
                            await ((Task)started).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                            reachedHealthyPeer = true;
                            break;
                        }
                        if (ReferenceEquals(await started, peers.Ordered[^1]))
                        {
                            reachedHealthyPeer = true;
                            break;
                        }
                    }
                    if (reachedHealthyPeer) break;
                    peers.Clock.Advance(TimeSpan.FromSeconds(10));
                }
                try
                {
                    await request;
                    supplied = true;
                }
                catch (RpcException exception) { TestContext.Out.WriteLine($"Request budget {budget + 1}: {exception.Message}"); }
            }
            finally
            {
                caller.Cancel();
                await request.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(supplied, Is.True, "authenticated healthy data must be reached within a bounded pool sweep");
            Assert.That(peers.Counts(context)[^1], Is.EqualTo(1));
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Execution_overall_deadline_does_not_cool_the_interrupted_peer([Values(AllocationContexts.Headers, AllocationContexts.Snap)] AllocationContexts context, CancellationToken token)
    {
        await using ExecutionPeers peers = new(5, snap1: context == AllocationContexts.Snap);
        foreach (ExecutionPeer peer in peers.Ordered)
            peer.BeforeResponse = static (_, cancellation) => Task.Delay(Timeout.Infinite, cancellation);
        Task request = peers.Request(context, token);
        for (int i = 0; i < 3; i++)
        {
            for (int hedge = 0; hedge < Math.Min(2, 5 - 2 * i); hedge++)
                await peers.Started.Reader.ReadAsync(token);
            peers.Clock.Advance(TimeSpan.FromSeconds(10));
        }
        Assert.That(async () => await request, Throws.TypeOf<RpcException>());
        foreach (ExecutionPeer peer in peers.Ordered.Take(4)) peers.Pool.RemovePeer(peer.SyncPeer);
        peers.Ordered[4].BeforeResponse = null;

        await peers.Request(context, token);

        Assert.That(peers.Counts(context), Is.EqualTo(new[] { 1, 1, 1, 1, 2 }));
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Execution_cursor_wraps_after_the_last_peer([Values(AllocationContexts.Headers, AllocationContexts.Snap)] AllocationContexts context, CancellationToken token)
    {
        await using ExecutionPeers peers = new(2, snap1: context == AllocationContexts.Snap);
        peers.Ordered[0].BeforeResponse = static (_, _) => Task.FromException(new IOException("Temporarily unavailable."));
        peers.Ordered[1].BeforeResponse = static (_, cancellation) => Task.Delay(Timeout.Infinite, cancellation);
        using CancellationTokenSource caller = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task request = peers.Request(context, caller.Token);
        await peers.Started.Reader.ReadAsync(token);
        await peers.Started.Reader.ReadAsync(token);
        caller.Cancel();
        Assert.That(async () => await request, Throws.InstanceOf<OperationCanceledException>());
        peers.Ordered[0].BeforeResponse = null;
        peers.Clock.Advance(TimeSpan.FromSeconds(60));

        await peers.Request(context, token);

        Assert.That(peers.Counts(context), Is.EqualTo(new[] { 2, 1 }));
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Execution_failures_are_scoped_to_the_requested_data(CancellationToken token)
    {
        await using ExecutionPeers peers = new(2);
        peers.Ordered[0].BeforeResponse = (context, _) => context == AllocationContexts.Receipts
            ? Task.FromException(new IOException("Receipts were pruned.")) : Task.CompletedTask;

        await peers.Request(AllocationContexts.Receipts, token);
        await peers.Request(AllocationContexts.Headers, token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(peers.Counts(AllocationContexts.Receipts), Is.EqualTo(new[] { 1, 1 }));
            Assert.That(peers.Counts(AllocationContexts.Headers), Is.EqualTo(new[] { 1, 0 }));
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Execution_cooldown_expires_and_removed_preferred_peers_are_replaced(
        [Values(AllocationContexts.Headers, AllocationContexts.Bodies, AllocationContexts.Receipts, AllocationContexts.BlockAccessLists, AllocationContexts.Snap)] AllocationContexts context,
        [Values] bool reconnect, CancellationToken token)
    {
        await using ExecutionPeers peers = new(3, snap1: context == AllocationContexts.Snap);
        peers.Ordered[0].BeforeResponse = static (_, _) => Task.FromException(new IOException("Temporarily unavailable."));
        await peers.Request(context, token);
        peers.Pool.RemovePeer(peers.Ordered[1].SyncPeer);
        if (reconnect) peers.Pool.AddPeer(peers.Ordered[1].SyncPeer);
        peers.Ordered[0].BeforeResponse = null;
        peers.Clock.Advance(TimeSpan.FromSeconds(60));

        if (context is AllocationContexts.Bodies or AllocationContexts.Receipts)
        {
            await peers.RequestPayload(context, 1, token);
            await peers.RequestPayload(context, 2, token);
        }
        else
        {
            await peers.Request(context, token);
            await peers.Request(context, token);
        }

        Assert.That(peers.Counts(context), Is.EqualTo(new[] { 3, 1, 0 }));
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Execution_local_request_capacity_does_not_cool_the_peer([Values(AllocationContexts.Headers, AllocationContexts.Snap)] AllocationContexts context, CancellationToken token)
    {
        await using ExecutionPeers peers = new(2, snap1: context == AllocationContexts.Snap);
        peers.Ordered[0].BeforeResponse = static (_, _) => Task.FromException(new ConcurrencyLimitReachedException("Local request limit."));
        await peers.Request(context, token);
        peers.Pool.RemovePeer(peers.Ordered[1].SyncPeer);
        peers.Ordered[0].BeforeResponse = null;

        await peers.Request(context, token);

        Assert.That(peers.Counts(context), Is.EqualTo(new[] { 2, 1 }));
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Execution_caller_cancellation_does_not_cool_the_peer([Values] bool queued, [Values(AllocationContexts.Headers, AllocationContexts.Snap)] AllocationContexts context, CancellationToken token)
    {
        int limit = context == AllocationContexts.Snap ? 8 : 4;
        await using ExecutionPeers peers = new(1, snap1: context == AllocationContexts.Snap);
        peers.Ordered[0].BeforeResponse = static (_, cancellation) => Task.Delay(Timeout.Infinite, cancellation);
        using CancellationTokenSource caller = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task[] active = queued ? Enumerable.Range(0, limit).Select(_ => peers.Request(context, caller.Token)).ToArray() : [];
        foreach (Task _ in active) await peers.Started.Reader.ReadAsync(token);
        using CancellationTokenSource cancelled = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task request = peers.Request(context, cancelled.Token);
        if (!queued) await peers.Started.Reader.ReadAsync(token);
        cancelled.Cancel();
        Assert.That(async () => await request, Throws.InstanceOf<OperationCanceledException>());
        caller.Cancel();
        foreach (Task running in active) Assert.That(async () => await running, Throws.InstanceOf<OperationCanceledException>());
        peers.Ordered[0].BeforeResponse = null;

        await peers.Request(context, token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(peers.Counts(context), Is.EqualTo(queued ? new[] { limit + 1 } : new[] { 2 }));
            Assert.That(peers.Pool.AllPeers.Single().SleepingContexts, Is.EqualTo(AllocationContexts.None));
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Peer_shutdown_cancels_active_and_queued_requests([Values(AllocationContexts.Headers, AllocationContexts.Snap)] AllocationContexts context, CancellationToken token)
    {
        int limit = context == AllocationContexts.Snap ? 8 : 4;
        ExecutionPeers peers = new(1, snap1: context == AllocationContexts.Snap);
        try
        {
            peers.Ordered[0].BeforeResponse = static (_, cancellation) => Task.Delay(Timeout.Infinite, cancellation);
            Task[] requests = Enumerable.Range(0, limit + 1).Select(_ => peers.Request(context, token)).ToArray();
            for (int i = 0; i < limit; i++) await peers.Started.Reader.ReadAsync(token);
            Assert.That(peers.Counts(context), Is.EqualTo(new[] { limit }), "the next request waits for its protocol request slot");

            await peers.DisposeAsync();

            foreach (Task request in requests) Assert.That(async () => await request, Throws.InstanceOf<OperationCanceledException>());
        }
        finally { await peers.DisposeAsync(); }
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Peer_queued_request_runs_after_capacity_is_released([Values(AllocationContexts.Headers, AllocationContexts.Snap)] AllocationContexts context, CancellationToken token)
    {
        int limit = context == AllocationContexts.Snap ? 8 : 4;
        await using ExecutionPeers peers = new(1, snap1: context == AllocationContexts.Snap);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        peers.Ordered[0].BeforeResponse = (_, cancellation) => release.Task.WaitAsync(cancellation);
        Task[] requests = Enumerable.Range(0, limit + 1).Select(_ => peers.Request(context, token)).ToArray();
        for (int i = 0; i < limit; i++) await peers.Started.Reader.ReadAsync(token);
        Assert.That(peers.Counts(context), Is.EqualTo(new[] { limit }));

        release.SetResult();
        await Task.WhenAll(requests);

        Assert.That(peers.Counts(context), Is.EqualTo(new[] { limit + 1 }));
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Peer_shutdown_waits_for_active_requests_to_unwind([Values] bool returnsResponse, [Values(AllocationContexts.Bodies, AllocationContexts.Snap)] AllocationContexts context, CancellationToken token)
    {
        ExecutionPeers peers = new(1, snap1: context == AllocationContexts.Snap);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        peers.Ordered[0].BeforeResponse = async (_, cancellation) =>
        {
            using CancellationTokenRegistration registration = cancellation.Register(() => cancelled.TrySetResult());
            try
            {
                if (returnsResponse) await release.Task;
                else await Task.Delay(Timeout.Infinite, cancellation);
            }
            finally { await release.Task; }
        };
        Task request = peers.Request(context, token);
        Task? shutdown = null;
        try
        {
            await peers.Started.Reader.ReadAsync(token);
            shutdown = peers.DisposeAsync().AsTask();
            await cancelled.Task.WaitAsync(token);
            Assert.That(shutdown.IsCompleted, Is.False, "the request still owns the execution permit");
            release.SetResult();
            Assert.That(async () => await request, Throws.InstanceOf<OperationCanceledException>());
            await shutdown;
            Assert.That(peers.Ordered[0].ResponsesDisposed, Is.EqualTo(returnsResponse ? 1 : 0));
        }
        finally
        {
            release.TrySetResult();
            await request.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (shutdown is not null) await shutdown;
            else await peers.DisposeAsync();
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Execution_BAL_capabilities_and_cooling_remain_distinct([Values] bool snap2, [Values] bool supported, CancellationToken token)
    {
        await using ExecutionPeers peers = new(1, snap2: snap2 && supported, ethVersion: supported && !snap2 ? (byte)71 : (byte)69);
        if (!supported)
        {
            Assert.That(async () => await peers.Request(AllocationContexts.BlockAccessLists, token), Throws.TypeOf<NotSupportedException>());
            Assert.That(peers.Counts(AllocationContexts.BlockAccessLists), Is.EqualTo(new[] { 0 }));
            return;
        }
        peers.Ordered[0].BeforeResponse = static (_, _) => Task.FromException(new IOException("BAL unavailable."));
        Task cooling = peers.Request(AllocationContexts.BlockAccessLists, token);
        await peers.Started.Reader.ReadAsync(token);
        peers.Clock.Advance(TimeSpan.FromSeconds(30));
        Assert.That(async () => await cooling, Throws.TypeOf<RpcException>());
        Assert.That(peers.Counts(AllocationContexts.BlockAccessLists), Is.EqualTo(new[] { 1 }));
        peers.Clock.Advance(TimeSpan.FromSeconds(30));
        peers.Ordered[0].BeforeResponse = null;

        await peers.Request(AllocationContexts.BlockAccessLists, token);

        Assert.That(peers.Counts(AllocationContexts.BlockAccessLists), Is.EqualTo(new[] { 2 }));
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Execution_ancestor_retries_dispose_rejected_pages([Values] bool brokenAncestry, CancellationToken token)
    {
        await using ExecutionPeers peers = new(2);
        peers.Ordered[0].InvalidAncestors = brokenAncestry;
        peers.Ordered[0].MissingAncestors = !brokenAncestry;

        Hash256[] hashes = await peers.Transport.GetAncestorHashesAsync(peers.Head, 0, token);
        await peers.Request(AllocationContexts.Headers, token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(hashes, Is.EqualTo(peers.Chain.Select(header => header.Hash)));
            Assert.That(peers.Counts(AllocationContexts.Headers), Is.EqualTo(new[] { 1, 2 }));
            Assert.That(peers.Ordered[0].ResponsesDisposed, Is.EqualTo(1));
            Assert.That(peers.Ordered[1].ResponsesDisposed, Is.EqualTo(1));
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Execution_rejects_invalid_authenticated_data_before_preferring_the_peer(
        [Values(AllocationContexts.Headers, AllocationContexts.Bodies, AllocationContexts.Receipts, AllocationContexts.BlockAccessLists, AllocationContexts.Snap)] AllocationContexts context,
        CancellationToken token)
    {
        await using ExecutionPeers peers = new(2, snap1: context == AllocationContexts.Snap);
        peers.Ordered[0].InvalidData = true;

        await peers.Request(context, token);
        await peers.Request(context, token);

        if (context is AllocationContexts.Bodies or AllocationContexts.Receipts)
        {
            Assert.That(peers.Counts(context), Is.EqualTo(new[] { 1, 1 }));
            await peers.RequestPayload(context, 1, token);
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(peers.Counts(context), Is.EqualTo(new[] { 1, 2 }));
            if (context != AllocationContexts.Headers)
                Assert.That(peers.Ordered[0].ResponsesDisposed, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task Light_client_snap_factory_answers_unavailable_without_losing_outbound_queries(
        [Values(1, 2)] int version,
        [Values(0, 2, 4)] int requestType)
    {
        await using ExecutionPeerTransport transport = SnapTransport();
        using SnapSession peer = new(Services(transport), version);
        using StorageRange storage = new()
        {
            RootHash = Hash256.Zero,
            Accounts = new ArrayPoolList<PathWithAccount>(1) { new(Keccak.Zero.ValueHash256, Account.TotallyEmpty) },
        };
        using P2PMessage request = requestType switch
        {
            0 => new GetAccountRangeMessage { RequestId = 42, AccountRange = new(Hash256.Zero, Keccak.Zero.ValueHash256), ResponseBytes = 1024 },
            2 => new GetStorageRangeMessage { RequestId = 42, StorageRange = storage.Copy(), ResponseBytes = 1024 },
            _ => new GetByteCodesMessage { RequestId = 42, Hashes = new ArrayPoolList<ValueHash256>(1) { Keccak.Zero.ValueHash256 }, Bytes = 1024 },
        };

        byte[] expected = requestType == 4 ? [0xc2, 42, 0xc0] : [0xc3, 42, 0xc0, 0xc0];
        await peer.AssertUnavailableThenQueryAsync(request, expected);
    }

    [Test]
    public async Task Light_client_snap1_factory_answers_unavailable_trie_nodes()
    {
        await using ExecutionPeerTransport transport = SnapTransport();
        using SnapSession peer = new(Services(transport), 1);
        using GetTrieNodesMessage request = new()
        {
            RequestId = 42,
            RootHash = Hash256.Zero,
            Paths = PathGroup.EncodeToRlpPathGroupList([new PathGroup { Group = [[0x11]] }]),
            Bytes = 1024,
        };

        await peer.AssertUnavailableThenQueryAsync(request, [0xc2, 42, 0xc0]);
    }

    [Test]
    public async Task Light_client_snap2_factory_preserves_positional_unavailable_bal_entries(
        [Values(0L, 1L, 2L, 3L, long.MaxValue)] long byteLimit)
    {
        await using ExecutionPeerTransport transport = SnapTransport();
        using SnapSession peer = new(Services(transport), 2);
        using GetBlockAccessListsMessage request = new()
        {
            RequestId = 42,
            BlockHashes = new ArrayPoolList<ValueHash256>(3) { Keccak.Zero.ValueHash256, Keccak.Compute("second").ValueHash256, Keccak.Compute("third").ValueHash256 },
            Bytes = byteLimit,
        };
        int count = (int)Math.Min(3, byteLimit);
        byte[] expected = new byte[3 + count];
        expected[0] = (byte)(0xc2 + count);
        expected[1] = 42;
        expected[2] = (byte)(0xc0 + count);
        expected.AsSpan(3).Fill(0x80);

        await peer.AssertUnavailableThenQueryAsync(request, expected);
    }

    [Test]
    public async Task Light_client_snap2_factory_answers_empty_bal_hash_list()
    {
        await using ExecutionPeerTransport transport = SnapTransport();
        using SnapSession peer = new(Services(transport), 2);
        using GetBlockAccessListsMessage request = new()
        {
            RequestId = 42,
            BlockHashes = IOwnedReadOnlyList<ValueHash256>.Empty,
            Bytes = 1024,
        };

        await peer.AssertUnavailableThenQueryAsync(request, [0xc2, 42, 0xc0]);
    }

    [Test]
    public async Task Light_client_snap_bal_unavailable_prefix_respects_budget_and_cancellation(
        [Values(-1L, 0L, 1L, 2L, 3L, long.MaxValue)] long byteLimit,
        [Values] bool canceled)
    {
        await using ExecutionPeerTransport transport = SnapTransport();
        ISnapServer server = Services(transport).Resolve<ISnapServer>();
        using CancellationTokenSource stop = new();
        if (canceled) stop.Cancel();
        using IByteArrayList result = server.GetBlockAccessLists([Keccak.Zero.ValueHash256, Keccak.Zero.ValueHash256, Keccak.Zero.ValueHash256], byteLimit, stop.Token);

        Assert.That(result.Count, Is.EqualTo(canceled ? 0 : Math.Clamp(byteLimit, 0, 3)));
        for (int i = 0; i < result.Count; i++) Assert.That(result[i].IsEmpty, Is.True);
    }

    [Test]
    public async Task Light_client_snap_bal_response_has_a_hard_allocation_bound()
    {
        await using ExecutionPeerTransport transport = SnapTransport();
        IReadOnlyList<ValueHash256> hashes = Substitute.For<IReadOnlyList<ValueHash256>>();
        hashes.Count.Returns((int)ISnapStateServer.HardResponseByteLimit + 1);

        using IByteArrayList result = Services(transport).Resolve<ISnapServer>()
            .GetBlockAccessLists(hashes, long.MaxValue, CancellationToken.None);

        Assert.That(result.Count, Is.EqualTo(ISnapStateServer.HardResponseByteLimit));
        Assert.That(result[^1].IsEmpty, Is.True);
    }

    [Test]
    public async Task Light_client_snap_hello_download_capabilities_do_not_advertise_enr_serving()
    {
        await using ExecutionPeerTransport transport = SnapTransport();
        IContainer services = Services(transport);
        SnapEnrAdvertisement enr = services.Resolve<SnapEnrAdvertisement>();
        IProtocolsManager manager = services.Resolve<IProtocolsManager>();
        Capability[] capabilities = (Capability[])typeof(ProtocolsManager)
            .GetMethod("GetAdvertisedCapabilities", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(manager, null)!;

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(NoopSnapServer.Instance.CanServe, Is.False);
        Assert.That(services.Resolve<ISyncConfig>().SnapServingEnabled, Is.False);
        Assert.That(enr.ServesSnap, Is.False);
        Assert.That(enr.ServesSnap2, Is.False);
        Assert.That(capabilities, Does.Contain(new Capability(Protocol.Snap, 1)));
        Assert.That(capabilities, Does.Contain(new Capability(Protocol.Snap, 2)));
    }

    private static ExecutionPeerTransport SnapTransport() => new("sepolia", Nethermind.Logging.LimboLogs.Instance,
        new VerifiedHead(0, 0, KnownHashes.SepoliaGenesis, Hash256.Zero), 1_800_000_000);

    private readonly record struct ReceiptRequest(ReceiptPeer Peer, long Id, Hash256 Hash, long Offset);

    private sealed class ReceiptPeers : IAsyncDisposable
    {
        private static readonly FieldInfo SoftLimit = typeof(SyncPeerProtocolHandlerBase)
            .GetField("SoftOutgoingMessageSizeLimit", BindingFlags.Static | BindingFlags.NonPublic)!;
        private readonly ulong _softLimit = (ulong)SoftLimit.GetValue(null)!;
        private readonly ExecutionPeers _execution = new(0);
        private bool _disposed;
        internal ExecutionPeerTransport Transport => _execution.Transport;
        internal ISyncServer Server { get; }
        internal ISyncPeerPool Pool => _execution.Pool;
        internal Dictionary<Hash256, (BlockHeader Header, TxReceipt[] Receipts)> Blocks { get; } = [];
        internal Channel<ReceiptRequest> Requests { get; } = Channel.CreateUnbounded<ReceiptRequest>();
        internal ReceiptPeer[] Sessions { get; }
        internal ReceiptPeer Primary { get; }
        internal ReceiptPeer Fallback { get; }

        internal ReceiptPeers(bool paged)
        {
            IContainer services = Services(Transport);
            Server = services.Resolve<ISyncServer>();
            BlockHeader initial = EmptyHeader();
            initial.Bloom = Bloom.Empty;
            initial.Hash = initial.CalculateHash();
            services.Resolve<IBlockTree>().Insert(initial, BlockTreeInsertHeaderOptions.TotalDifficultyNotNeeded);
            Sessions = [new(this, services, initial, 1001), new(this, services, initial, 1002)];
            foreach (ReceiptPeer peer in Sessions) Pool.AddPeer(peer.SyncPeer);
            Primary = Sessions.Single(peer => ReferenceEquals(peer.SyncPeer, Pool.InitializedPeers.First().SyncPeer));
            Fallback = Sessions.Single(peer => !ReferenceEquals(peer, Primary));
            Primary.Paged = paged;
            foreach (ReceiptPeer peer in Sessions) peer.Respond = request => Reply(request);
            SoftLimit.SetValue(null, 75UL);
        }

        internal BlockHeader AddBlock(ulong number)
        {
            BlockHeader header = EmptyHeader();
            header.Number = number;
            header.GasLimit = 1_000_000;
            header.GasUsed = 42_000;
            TxReceipt[] receipts = Enumerable.Range(1, 2).Select(index => new TxReceipt
            {
                StatusCode = 1,
                BlockNumber = number,
                GasUsedTotal = (ulong)index * 21_000,
                Logs = [new LogEntry(Address.Zero, [(byte)index], [])],
            }).ToArray();
            foreach (TxReceipt receipt in receipts) receipt.Bloom = new Bloom(receipt.Logs);
            header.Bloom = new Bloom(receipts.SelectMany(receipt => receipt.Logs!).ToArray());
            header.TxRoot = TxTrie.CalculateRoot(Enumerable.Range(1, 2).Select(index => new Transaction
            {
                Nonce = (ulong)index, To = Address.Zero, GasLimit = 30_000, GasPrice = 1,
            }).ToArray());
            header.ReceiptsRoot = ReceiptTrie.CalculateRoot(Transport.SpecProvider.GetSpec(header), receipts, new ReceiptMessageDecoder());
            header.Hash = header.CalculateHash();
            Blocks.Add(header.Hash!, (header, receipts));
            return header;
        }

        internal void Reply(ReceiptRequest request, TxReceipt[]? receipts = null)
        {
            receipts ??= Blocks[request.Hash].Receipts;
            bool partial = request.Peer.Paged && request.Offset == 0;
            TxReceipt[] page = partial ? [receipts[0]] : receipts.Skip(checked((int)request.Offset)).ToArray();
            request.Peer.Reply(request, page, partial);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                ValueTask stopping = _execution.DisposeAsync();
                try
                {
                    foreach (ReceiptPeer peer in Sessions) peer.CompletePending();
                }
                finally { await stopping; }
            }
            finally
            {
                try
                {
                    foreach (ReceiptPeer peer in Sessions) peer.Dispose();
                }
                finally { SoftLimit.SetValue(null, _softLimit); }
            }
        }
    }

    private sealed class ReceiptPeer : IDisposable
    {
        private readonly ISession _session = Substitute.For<ISession>();
        private readonly IMessageSerializationService _serializer;
        private readonly IZeroProtocolHandler _handler;
        private readonly ConcurrentQueue<IByteBuffer> _buffers = new();
        private readonly ConcurrentDictionary<long, ReceiptRequest> _pending = new();
        private int _bodyRequests;
        internal ISyncPeer SyncPeer => (ISyncPeer)_handler;
        internal ConcurrentQueue<ReceiptRequest> Requests { get; } = new();
        internal ConcurrentQueue<Exception> WireErrors { get; } = new();
        internal Action<ReceiptRequest>? Respond;
        internal bool Paged;
        internal int BodyRequests => Volatile.Read(ref _bodyRequests);
        internal bool BuffersReleased => _buffers.All(buffer => buffer.ReferenceCount == 0);

        internal ReceiptPeer(ReceiptPeers owner, IContainer services, BlockHeader initial, int identity)
        {
            _serializer = services.Resolve<IMessageSerializationService>();
            using PrivateKey key = new(((UInt256)identity).ToBigEndian());
            _session.Node.Returns(new Node(key.PublicKey, "127.0.0.1", 30303));
            _handler = CreateProtocolHandler(services, _session, "eth", 70);
            _session.When(session => session.DeliverMessage(Arg.Any<P2PMessage>())).Do(call =>
            {
                using P2PMessage message = call.Arg<P2PMessage>();
                switch (message)
                {
                    case GetReceiptsMessage70 receipts:
                        ReceiptRequest request = new(this, receipts.RequestId, receipts.Hashes[0], receipts.FirstBlockReceiptIndex);
                        _pending.TryAdd(request.Id, request);
                        Requests.Enqueue(request);
                        owner.Requests.Writer.TryWrite(request);
                        Respond?.Invoke(request);
                        break;
                    case Eth66Messages.GetBlockHeadersMessage headers:
                        BlockHeader header = headers.EthMessage.StartBlockHash is { } hash && owner.Blocks.TryGetValue(hash, out (BlockHeader Header, TxReceipt[] Receipts) block)
                            ? block.Header : initial;
                        using (Eth66Messages.BlockHeadersMessage reply = new(headers.RequestId,
                            new Eth62Messages.BlockHeadersMessage(new ArrayPoolList<BlockHeader>(1) { header }))) Deliver(reply);
                        break;
                    case Eth66Messages.GetBlockBodiesMessage bodies:
                        Interlocked.Increment(ref _bodyRequests);
                        using (Eth66Messages.BlockBodiesMessage reply = new(bodies.RequestId, new Eth62Messages.BlockBodiesMessage(Array.Empty<BlockBody>()))) Deliver(reply);
                        break;
                }
            });
            _handler.Init();
            using StatusMessage69 status = new()
            {
                ProtocolVersion = 70, NetworkId = owner.Server.NetworkId, GenesisHash = owner.Server.Genesis.Hash!,
                LatestBlock = initial.Number, LatestBlockHash = initial.Hash!,
                ForkId = services.Resolve<IForkInfo>().GetForkId(initial.Number, initial.Timestamp),
            };
            Deliver(status);
            SyncPeer.IsInitialized = true;
        }

        internal void Reply(ReceiptRequest request, TxReceipt[] receipts, bool partial)
        {
            if (!_pending.TryRemove(request.Id, out _)) throw new InvalidOperationException("ETH fixture request is no longer pending.");
            try { SendReply(request, receipts, partial); }
            catch
            {
                _pending.TryAdd(request.Id, request);
                throw;
            }
        }

        private void SendReply(ReceiptRequest request, TxReceipt[] receipts, bool partial)
        {
            ArrayPoolList<TxReceipt[]> blocks = new(1);
            if (receipts.Length != 0) blocks.Add(receipts);
            using ReceiptsMessage70 reply = new(request.Id, blocks, partial);
            Deliver(reply);
        }

        internal void CompletePending()
        {
            foreach (KeyValuePair<long, ReceiptRequest> pending in _pending)
                if (_pending.TryRemove(pending.Key, out ReceiptRequest request)) SendReply(request, [], false);
        }

        private void Deliver(P2PMessage message)
        {
            IByteBuffer buffer = message switch
            {
                StatusMessage69 value => _serializer.ZeroSerialize(value),
                ReceiptsMessage70 value => _serializer.ZeroSerialize(value),
                Eth66Messages.BlockHeadersMessage value => _serializer.ZeroSerialize(value),
                Eth66Messages.BlockBodiesMessage value => _serializer.ZeroSerialize(value),
                _ => throw new ArgumentException("Unexpected ETH test message.", nameof(message)),
            };
            _buffers.Enqueue(buffer);
            buffer.ReadByte();
            ZeroPacket packet = new(buffer) { PacketType = (byte)message.PacketType };
            try { _handler.HandleMessage(packet); }
            finally { ReferenceCountUtil.Release(packet); }
        }

        public void Dispose() => _handler.Dispose();
    }

    private static IZeroProtocolHandler CreateProtocolHandler(IContainer services, ISession session, string protocol, int version)
    {
        IProtocolHandler? created = null;
        foreach (IProtocolHandlerFactory factory in services.Resolve<IProtocolHandlerFactory[]>())
            if (factory.ProtocolCode == protocol && factory.TryCreate(session, version, out created)) break;
        Assert.That(created, Is.AssignableTo<IZeroProtocolHandler>());
        return (IZeroProtocolHandler)created!;
    }

    private sealed class SnapSession : IDisposable
    {
        private readonly ISession _session = Substitute.For<ISession>();
        private readonly IMessageSerializationService _serializer;
        private readonly IZeroProtocolHandler _handler;
        private readonly TaskCompletionSource<(int Type, byte[] Rlp)> _response = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal SnapSession(IContainer services, int version)
        {
            _serializer = services.Resolve<IMessageSerializationService>();
            using PrivateKey key = new("0x0000000000000000000000000000000000000000000000000000000000000001");
            _session.Node.Returns(new Node(key.PublicKey, "127.0.0.1", 30303));
            _session.When(session => session.InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>()))
                .Do(call => _response.TrySetException(new InvalidOperationException($"Unexpected disconnect: {call[0]} {call[1]}")));
            _handler = CreateProtocolHandler(services, _session, Protocol.Snap, version);
            _session.When(session => session.DeliverMessage(Arg.Any<P2PMessage>())).Do(call =>
            {
                using P2PMessage message = call.Arg<P2PMessage>();
                if (message is GetByteCodesMessage outgoing)
                {
                    using ByteCodesMessage reply = new(new ByteArrayListAdapter(new ArrayPoolList<byte[]>(1) { new byte[] { 0x60, 0x00 } }))
                    {
                        RequestId = outgoing.RequestId,
                    };
                    Deliver(reply);
                }
                else
                {
                    IByteBuffer buffer = Serialize(message);
                    try
                    {
                        buffer.ReadByte();
                        byte[] rlp = new byte[buffer.ReadableBytes];
                        buffer.ReadBytes(rlp);
                        _response.TrySetResult((message.PacketType, rlp));
                    }
                    finally { buffer.Release(); }
                }
            });
        }

        internal async Task AssertUnavailableThenQueryAsync(P2PMessage request, byte[] expected)
        {
            Deliver(request);
            (int type, byte[] rlp) = await _response.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(type, Is.EqualTo(request.PacketType + 1));
                Assert.That(rlp, Is.EqualTo(expected));
            }
            using IByteArrayList code = await ((ISnapSyncPeer)_handler)
                .GetByteCodes([Keccak.Compute("requested code").ValueHash256], CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(code.Count, Is.EqualTo(1));
            Assert.That(code[0].ToArray(), Is.EqualTo(new byte[] { 0x60, 0x00 }));
            _session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
        }

        private void Deliver(P2PMessage message)
        {
            IByteBuffer buffer = Serialize(message);
            buffer.ReadByte();
            ZeroPacket packet = new(buffer) { PacketType = (byte)message.PacketType };
            try { _handler.HandleMessage(packet); }
            finally { ReferenceCountUtil.Release(packet); }
        }

        public void Dispose() => _handler.Dispose();

        private IByteBuffer Serialize(P2PMessage message) => message switch
        {
            GetAccountRangeMessage value => _serializer.ZeroSerialize(value),
            AccountRangeMessage value => _serializer.ZeroSerialize(value),
            GetStorageRangeMessage value => _serializer.ZeroSerialize(value),
            StorageRangeMessage value => _serializer.ZeroSerialize(value),
            GetByteCodesMessage value => _serializer.ZeroSerialize(value),
            ByteCodesMessage value => _serializer.ZeroSerialize(value),
            GetTrieNodesMessage value => _serializer.ZeroSerialize(value),
            TrieNodesMessage value => _serializer.ZeroSerialize(value),
            GetBlockAccessListsMessage value => _serializer.ZeroSerialize(value),
            BlockAccessListsMessage value => _serializer.ZeroSerialize(value),
            _ => throw new ArgumentException("Unexpected SNAP test message.", nameof(message)),
        };
    }

    [Test]
    public async Task Execution_data_directory_scopes_inherited_paths_and_preserves_memory_databases(
        [Values(null, "relative", "absolute")] string? directoryKind)
    {
        string directory = PeerTestDirectory();
        string? dataDirectory = directoryKind switch
        {
            "relative" => Path.GetRelativePath(Environment.CurrentDirectory, directory),
            "absolute" => directory,
            _ => null,
        };
        await using ExecutionPeerTransport transport = new("sepolia", Nethermind.Logging.LimboLogs.Instance,
            new VerifiedHead(0, 0, KnownHashes.SepoliaGenesis, Hash256.Zero), 0, dataDirectory);
        IContainer services = Services(transport);
        IInitConfig configuration = services.Resolve<IInitConfig>();
        string expected = Path.Combine(directory, "el", "sepolia");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(configuration.DataDir, Is.EqualTo(dataDirectory is null
                ? "src/Nethermind/artifacts/lightclient-execution" : expected));
            Assert.That(configuration.BaseDbPath, Is.EqualTo(dataDirectory is null ? "db" : Path.Combine(expected, "db")));
            Assert.That(configuration.DiagnosticMode, Is.EqualTo(DiagnosticMode.MemDb));
            Assert.That(services.ResolveKeyed<IDb>(DbNames.Headers), Is.TypeOf<MemDb>());
        }
    }

    [Test]
    public async Task Execution_port_configures_both_tcp_and_discovery([Values(1, 30317, 65535)] int port)
    {
        await using ExecutionPeerTransport transport = new("sepolia", Nethermind.Logging.LimboLogs.Instance,
            new VerifiedHead(0, 0, KnownHashes.SepoliaGenesis, Hash256.Zero), 0, port: port);
        INetworkConfig configuration = Services(transport).Resolve<INetworkConfig>();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(configuration.P2PPort, Is.EqualTo(port));
            Assert.That(configuration.DiscoveryPort, Is.EqualTo(port));
        }
    }

    [Test]
    public async Task Execution_startup_cancels_before_network_binding([Values] bool alreadyCanceled)
    {
        await using ExecutionPeerTransport transport = new("mainnet", Nethermind.Logging.LimboLogs.Instance,
            new VerifiedHead(0, 0, KnownHashes.MainnetGenesis, Hash256.Zero), 0);
        IPResolver resolver = (IPResolver)Services(transport).Resolve<IIPResolver>();
        TaskCompletionSource<IIPResolver.NethermindIp> resolution = new(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(IPResolver).GetField("_resolveTask", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(resolver, resolution.Task);
        using CancellationTokenSource stop = new();
        if (alreadyCanceled) stop.Cancel();
        Task start = transport.StartAsync(stop.Token);
        try
        {
            if (!alreadyCanceled) Assert.That(start.IsCompleted, Is.False);
            stop.Cancel();
            Assert.That(async () => await start.WaitAsync(TimeSpan.FromSeconds(5)), Throws.InstanceOf<OperationCanceledException>());
            Assert.That(resolution.Task.IsCompleted, Is.False, "Cancellation must not wait for external IP resolution.");
        }
        finally
        {
            resolution.TrySetException(new IOException("Test IP resolution stopped."));
            _ = resolution.Task.Exception;
            try { await start; }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }
    }

    [Test]
    public async Task Production_discovery_storage_restarts_and_revalidates_signed_enrs([Values] bool tamper)
    {
        string directory = PeerTestDirectory();
        try
        {
            using PrivateKey key = new("0x0000000000000000000000000000000000000000000000000000000000000001");
            NodeRecord record = new();
            record.SetEntry(new SecP256k1Entry(key.CompressedPublicKey));
            record.SetEntry(new IpEntry(IPAddress.Parse("8.8.8.8")));
            record.SetEntry(new TcpEntry(30303));
            record.SetEntry(new UdpEntry(30303));
            new NodeRecordSigner(new Ecdsa(), key).Sign(record);
            NetworkNode node = new(record.ToString());
            await using (ExecutionPeerTransport first = new("mainnet", Nethermind.Logging.LimboLogs.Instance,
                new VerifiedHead(0, 0, KnownHashes.MainnetGenesis, Hash256.Zero), 0, directory))
            {
                INetworkStorage storage = Services(first).ResolveKeyed<INetworkStorage>(DbNames.DiscoveryV5Nodes);
                storage.StartBatch();
                storage.UpdateNodes([node]);
                storage.Commit();
            }
            if (tamper)
            {
                node.Enr!.Signature = new Signature(new byte[64], 0);
                string persisted = Convert.ToHexString(key.PublicKey.Bytes) + ","
                    + Convert.ToHexString(NetworkNodeDecoder.Instance.EncodeAsBytes(node)) + Environment.NewLine;
                await File.WriteAllTextAsync(Path.Combine(directory, "el/mainnet/discovery", SimpleFilePublicKeyDb.DbFileName), persisted);
            }
            await using ExecutionPeerTransport resumed = new("mainnet", Nethermind.Logging.LimboLogs.Instance,
                new VerifiedHead(0, 0, KnownHashes.MainnetGenesis, Hash256.Zero), 0, directory);
            NetworkNode[] restored = Services(resumed).ResolveKeyed<INetworkStorage>(DbNames.DiscoveryV5Nodes).GetPersistedNodes();
            if (tamper) Assert.That(restored, Is.Empty);
            else
            {
                Assert.That(restored.Single().NodeId, Is.EqualTo(key.PublicKey));
                Assert.That(restored[0].IsEnr, Is.True);
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Test]
    public async Task Oversized_discovery_cache_is_preserved_and_does_not_block_startup()
    {
        string directory = PeerTestDirectory();
        string cacheDirectory = Path.Combine(directory, "el/mainnet/discovery");
        Directory.CreateDirectory(cacheDirectory);
        string path = Path.Combine(cacheDirectory, SimpleFilePublicKeyDb.DbFileName);
        try
        {
            await File.WriteAllBytesAsync(path, new byte[2 * 1024 * 1024 + 1]);
            await using ExecutionPeerTransport transport = new("mainnet", Nethermind.Logging.LimboLogs.Instance,
                new VerifiedHead(0, 0, KnownHashes.MainnetGenesis, Hash256.Zero), 0, directory);
            INetworkStorage storage = Services(transport).ResolveKeyed<INetworkStorage>(DbNames.DiscoveryV5Nodes);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(storage.GetPersistedNodes(), Is.Empty);
                Assert.That(File.Exists(path), Is.False);
                Assert.That(Directory.GetFiles(cacheDirectory, "*.invalid-*"), Has.Length.EqualTo(1));
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static IContainer Services(ExecutionPeerTransport transport) => (IContainer)typeof(ExecutionPeerTransport)
        .GetField("_services", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(transport)!;

    private static string PeerTestDirectory() => Path.Combine(TestContext.CurrentContext.WorkDirectory, "artifacts",
        "NethermindLightClientPeerTests", Guid.NewGuid().ToString("N"));

    [Test]
    public void Discovery_snapshot_retains_only_a_bounded_set_of_signed_records()
    {
        using MemDb db = new();
        NetworkStorage storage = new(db, Nethermind.Logging.LimboLogs.Instance);
        BoundedEnrStorage bounded = new(storage);
        NetworkNode[] nodes = new NetworkNode[BoundedEnrStorage.Capacity + 1];
        for (int i = 0; i < nodes.Length; i++)
        {
            using PrivateKey key = new(((UInt256)(i + 1)).ToBigEndian());
            NodeRecord record = new();
            record.SetEntry(new SecP256k1Entry(key.CompressedPublicKey));
            new NodeRecordSigner(new Ecdsa(), key).Sign(record);
            nodes[i] = new(record.ToString());
        }
        bounded.StartBatch();
        bounded.UpdateNodes(nodes);
        bounded.Commit();
        Assert.That(bounded.GetPersistedNodes(), Has.Length.EqualTo(BoundedEnrStorage.Capacity));
        bounded.StartBatch();
        bounded.UpdateNodes([nodes[^1]]);
        bounded.Commit();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(bounded.GetPersistedNodes().Single().NodeId, Is.EqualTo(nodes[^1].NodeId));
            Assert.That(new NetworkStorage(db, Nethermind.Logging.LimboLogs.Instance).GetPersistedNodes().Single().NodeId, Is.EqualTo(nodes[^1].NodeId));
        }
    }

    [Test]
    public async Task Relay_pool_interface_serves_verified_blob_sidecars_and_cells()
    {
        await KzgPolynomialCommitments.InitializeAsync(Nethermind.Logging.LimboLogs.Instance.GetClassLogger<TransportTests>());
        IBlobProofsManager proofs = IBlobProofsManager.For(ProofVersion.V1);
        ShardBlobNetworkWrapper wrapper = proofs.AllocateWrapper(new byte[131_072]);
        proofs.ComputeProofsAndCommitments(wrapper);
        Transaction transaction = new()
        {
            Type = TxType.Blob,
            ChainId = 1,
            Nonce = 7,
            GasLimit = 21_000,
            DecodedMaxFeePerGas = 10,
            GasPrice = 2,
            MaxFeePerBlobGas = 2,
            To = Address.Zero,
            BlobVersionedHashes = proofs.ComputeHashes(wrapper),
            NetworkWrapper = wrapper,
        };
        using PrivateKey key = new("0x0000000000000000000000000000000000000000000000000000000000000001");
        new EthereumEcdsa(1).Sign(key, transaction);
        transaction.Hash = transaction.CalculateHash();
        using BoundedTransactionRelay relay = new();
        relay.AddVerified(transaction);
        ITxPool pool = relay;
        Assert.That(pool.TryGetPendingTransaction(transaction.Hash, out Transaction? served), Is.True);
        Assert.That(pool.TryGetPendingTransactionWithoutBlobs(transaction.Hash, out Transaction? metadata), Is.True);
        Assert.That(pool.TryGetBlobCellsAndProofsV1(transaction.BlobVersionedHashes[0]!, BlobCellMask.FromIndices([0, 127]),
            out BlobCellMask mask, out byte[][]? cells, out byte[][]? selectedProofs), Is.True);
        ShardBlobNetworkWrapper partial = wrapper with { Blobs = [], CellMask = mask, Cells = cells };
        using (Assert.EnterMultipleScope())
        {
            Assert.That(pool.SupportsBlobs, Is.True);
            Assert.That(pool.IsKnown(transaction.Hash), Is.True);
            Assert.That(served, Is.SameAs(transaction));
            Assert.That(((ShardBlobNetworkWrapper)metadata!.NetworkWrapper!).Blobs, Is.Empty);
            Assert.That(pool.GetPendingBlobTransactionsCount(), Is.EqualTo(1));
            Assert.That(pool.GetPendingTransactionsCount(), Is.Zero);
            Assert.That(BlobCellsHelper.ValidateCells(partial), Is.True);
            Assert.That(selectedProofs, Is.EqualTo(new[] { wrapper.Proofs[0], wrapper.Proofs[127] }));
        }
        Assert.That(pool.RemoveTransaction(transaction.Hash), Is.True);
        Assert.That(pool.TryGetPendingTransaction(transaction.Hash, out _), Is.False);
    }

    [Test]
    public async Task Peer_serving_scheduler_bounds_queue_and_cleans_up_shutdown()
    {
        LightPeerRequestScheduler scheduler = new(Nethermind.Logging.LimboLogs.Instance);
        TaskCompletionSource occupied = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int active = 0;
        int cleaned = 0;
        Task Run(ServingRequest _, CancellationToken token)
        {
            if (Interlocked.Increment(ref active) == 2) occupied.TrySetResult();
            return release.Task.WaitAsync(token);
        }
        Assert.That(scheduler.TryScheduleTask(new ServingRequest(), Run, TimeSpan.FromHours(1)), Is.True);
        Assert.That(scheduler.TryScheduleTask(new ServingRequest(), Run, TimeSpan.FromHours(1)), Is.True);
        await occupied.Task;
        for (int i = 0; i < 32; i++)
            Assert.That(scheduler.TryScheduleTask(new ServingRequest(), (_, token) =>
            {
                Assert.That(token.IsCancellationRequested, Is.True);
                Interlocked.Increment(ref cleaned);
                return Task.CompletedTask;
            }), Is.True);
        Assert.That(scheduler.TryScheduleTask(new ServingRequest(), Run), Is.False);
        await scheduler.DisposeAsync();
        Assert.That(cleaned, Is.EqualTo(32));
        Assert.That(scheduler.TryScheduleTask(new ServingRequest(), Run), Is.False);
    }

    private sealed record ServingRequest : IBackgroundTaskRequest<ServingRequest>;

    [Test]
    [CancelAfter(30_000)]
    public async Task BAL_requests_follow_the_target_fork_when_started_before_activation(
        [Values(-1, 0, 1)] int offset, CancellationToken token)
    {
        ChainSpec chainSpec = new ChainSpecFileLoader(new EthereumJsonSerializer(), Nethermind.Logging.LimboLogs.Instance)
            .LoadEmbeddedOrFromFile("sepolia.json");
        ulong activation = chainSpec.Parameters.Eip7928TransitionTimestamp!.Value;
        await using ExecutionPeers peers = new(1, startupTimestamp: activation - 1);
        BlockHeader header = peers.Header;
        header.Timestamp = (ulong)((long)activation + offset);
        header.Hash = header.CalculateHash();

        Assert.That(Services(peers.Transport).Resolve<IProtocolsManager>().GetHighestProtocolVersion(Protocol.Snap), Is.EqualTo(2));
        if (offset < 0)
            Assert.That(async () => await peers.Transport.GetBlockAccessListAsync(header, token), Throws.TypeOf<NotSupportedException>());
        else
            Assert.That((await peers.Transport.GetBlockAccessListAsync(header, token)).WireHash, Is.EqualTo(header.BlockAccessListHash));
        Assert.That(peers.Counts(AllocationContexts.BlockAccessLists), Is.EqualTo(new[] { offset < 0 ? 0 : 1 }));
    }

    [Test]
    public async Task Execution_transport_uses_discv5_without_direct_bootnode_candidates()
    {
        await using ExecutionPeerTransport transport = new("mainnet", Nethermind.Logging.LimboLogs.Instance,
            new VerifiedHead(0, 0, KnownHashes.MainnetGenesis, Hash256.Zero), 0);
        IContainer services = Services(transport);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(services.Resolve<IDiscoveryConfig>().DiscoveryVersion, Is.EqualTo(DiscoveryVersion.V5));
            Assert.That(services.Resolve<INetworkConfig>().EnableExternalIpResolution, Is.True);
            Assert.That(services.Resolve<IIPResolver>(), Is.TypeOf<IPResolver>());
            Assert.That(services.Resolve<NodesLoaderOptions>().LoadBootnodesAsPeerCandidates, Is.False);
        }

        await foreach (Node node in services.Resolve<NodesLoader>().DiscoverNodes(default))
            Assert.That(node.IsBootnode, Is.False);
    }

    [Test]
    public async Task Execution_discovery_can_start_with_the_real_genesis_before_consensus_bootstrap(
        [Values("mainnet", "sepolia", "hoodi")] string network)
    {
        await using ExecutionPeerTransport transport = new(network, Nethermind.Logging.LimboLogs.Instance);
        ISyncServer server = Services(transport).Resolve<ISyncServer>();
        Hash256 expected = network switch
        {
            "mainnet" => KnownHashes.MainnetGenesis,
            "sepolia" => KnownHashes.SepoliaGenesis,
            "hoodi" => KnownHashes.HoodiGenesis,
            _ => throw new ArgumentOutOfRangeException(nameof(network)),
        };

        Assert.That(server.Head, Is.SameAs(server.Genesis));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(server.Head!.Number, Is.Zero);
            Assert.That(server.Head.CalculateHash(), Is.EqualTo(expected));
            Assert.That(server.Genesis.Hash, Is.EqualTo(expected));
            Assert.That(server.Head.TotalDifficulty, Is.EqualTo(server.Genesis.Difficulty));
            Assert.That(server.Head.Timestamp, Is.EqualTo(server.Genesis.Timestamp));
            Assert.That(server.Head.StateRoot, Is.EqualTo(server.Genesis.StateRoot));
        }
    }

    [Test]
    public async Task Unresolved_Gloas_heads_preserve_the_last_known_execution_status([Values] bool updateHead)
    {
        ulong timestamp = BeaconChainSpec.Sepolia.GenesisTime + 11_311_456 * BeaconChainSpec.Sepolia.SecondsPerSlot;
        VerifiedHead unresolved = new(11_311_456, 0, Keccak.Compute("authenticated execution header"), Hash256.Zero);
        await using ExecutionPeerTransport transport = new("sepolia", Nethermind.Logging.LimboLogs.Instance,
            updateHead ? null : unresolved, timestamp);
        ISyncServer server = Services(transport).Resolve<ISyncServer>();
        IForkInfo forks = Services(transport).Resolve<IForkInfo>();
        if (updateHead) transport.UpdateHead(unresolved, timestamp);

        Assert.That(server.Head, Is.SameAs(server.Genesis));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(server.Head!.Number, Is.Zero);
            Assert.That(server.Head.Hash, Is.EqualTo(KnownHashes.SepoliaGenesis));
            Assert.That(server.Head.CalculateHash(), Is.EqualTo(KnownHashes.SepoliaGenesis));
            Assert.That(server.Head.Timestamp, Is.EqualTo(0x6159af19UL));
            Assert.That(forks.GetForkId(server.Head.Number, server.Head.Timestamp), Is.EqualTo(new Nethermind.Network.ForkId(0xfe3366e7, 1_735_371)));
        }
        VerifiedHead resolved = unresolved with { Number = 12_345_678, StateRoot = Keccak.Compute("verified state root") };

        transport.UpdateHead(resolved, timestamp);

        BlockHeader published = server.Head!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(published.Number, Is.EqualTo(resolved.Number));
            Assert.That(published.Hash, Is.EqualTo(resolved.BlockHash));
            Assert.That(published.StateRoot, Is.EqualTo(resolved.StateRoot));
            Assert.That(published.Timestamp, Is.EqualTo(timestamp));
            Assert.That(forks.GetForkId(published.Number, published.Timestamp), Is.EqualTo(new Nethermind.Network.ForkId(0x6c1d9423, 0)));
        }

        transport.UpdateHead(unresolved with { Slot = unresolved.Slot + 1, BlockHash = Keccak.Compute("next unresolved head") }, timestamp + 12);

        Assert.That(server.Head, Is.SameAs(published));
    }

    [Test]
    public void Verified_body_rejects_missing_transactions_or_withdrawals([Values] bool wrongTransactions)
    {
        BlockBody body = new([], [], []);
        BlockHeader header = EmptyHeader();
        if (wrongTransactions) header.TxRoot = Keccak.Compute("unavailable transaction");
        else header.WithdrawalsRoot = Keccak.Compute("unavailable withdrawal");

        Assert.Throws<InvalidDataException>(() => ExecutionPeerTransport.VerifyBlockBody(header, body));
    }

    [Test]
    public void Verified_receipts_reject_inconsistent_root([Values] bool wrongRoot)
    {
        BlockHeader header = EmptyHeader();
        if (wrongRoot) header.ReceiptsRoot = Keccak.Compute("unavailable receipt");

        if (wrongRoot)
            Assert.Throws<InvalidDataException>(() => ExecutionPeerTransport.VerifyReceipts(header, [], MainnetSpecProvider.Instance));
        else
            Assert.DoesNotThrow(() => ExecutionPeerTransport.VerifyReceipts(header, [], MainnetSpecProvider.Instance));
    }

    private static BlockHeader EmptyHeader() => new(Keccak.Zero, UnclesHash.Calculate([]), Address.Zero,
        UInt256.Zero, 10, 30_000_000, 1_700_000_000, [])
    {
        TxRoot = Keccak.EmptyTreeHash,
        ReceiptsRoot = Keccak.EmptyTreeHash,
        WithdrawalsRoot = WithdrawalTrie.CalculateRoot([]),
    };

    [TestCase("{", -32700)]
    [TestCase("[]", -32600)]
    [TestCase("42", -32600)]
    [TestCase("{\"jsonrpc\":2,\"method\":\"eth_chainId\",\"id\":1}", -32600)]
    [TestCase("{\"jsonrpc\":\"2.0\",\"method\":\"eth_chainId\",\"id\":true}", -32600)]
    [TestCase("{\"jsonrpc\":\"2.0\",\"method\":\"eth_sendRawTransaction\",\"id\":1}", -32601)]
    public async Task Rpc_endpoint_rejects_invalid_requests(string request, int code)
    {
        DefaultHttpContext context = await InvokeAsync(request);
        using JsonDocument response = JsonDocument.Parse(((MemoryStream)context.Response.Body).ToArray());
        Assert.That(response.RootElement.GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(code));
    }

    [Test]
    public async Task Rpc_batch_preserves_ids_and_suppresses_notifications()
    {
        DefaultHttpContext context = await InvokeAsync("[{\"jsonrpc\":\"2.0\",\"method\":\"eth_chainId\",\"id\":\"first\"},{\"jsonrpc\":\"2.0\",\"method\":\"eth_chainId\"},{\"jsonrpc\":\"2.0\",\"method\":\"unknown\",\"id\":null}]");
        using JsonDocument response = JsonDocument.Parse(((MemoryStream)context.Response.Body).ToArray());
        Assert.That(response.RootElement.GetArrayLength(), Is.EqualTo(2));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.RootElement[0].GetProperty("id").GetString(), Is.EqualTo("first"));
            Assert.That(response.RootElement[0].GetProperty("result").GetString(), Is.EqualTo("0x1"));
            Assert.That(response.RootElement[1].GetProperty("id").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(response.RootElement[1].GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(-32601));
        }
    }

    [Test]
    public async Task Batch_byte_budget_preserves_successes_and_does_not_execute_remaining_items([Values] bool overBoundary)
    {
        int firstTokenBytes = BoundedJson.ResultLimit;
        int envelopeBytes = JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id = 1, result = "" }).Length - 2;
        int secondTokenBytes = RpcEndpoint.BatchResponseLimit - RpcEndpoint.BatchErrorReserve - 3
            - firstTokenBytes - 2 * envelopeBytes + (overBoundary ? 1 : 0);
        using JsonDocument first = JsonDocument.Parse("\"" + new string('a', firstTokenBytes - 2) + "\"");
        using JsonDocument second = JsonDocument.Parse("\"" + new string('b', secondTokenBytes - 2) + "\"");
        using JsonDocument requests = JsonDocument.Parse("""
            [{"jsonrpc":"2.0","method":"first","id":1},
             {"jsonrpc":"2.0","method":"second","id":2},
             {"jsonrpc":"2.0","method":"later","id":"retained"},
             {"jsonrpc":"2.0","method":"notification"},
             {"jsonrpc":"2.0","method":"invalid","id":true}]
            """);
        int executions = 0;
        RecordingLogger logger = new();
        DefaultHttpContext context = new();
        context.Response.Body = new MemoryStream();
        await RpcEndpoint.WriteBatchAsync(context, requests.RootElement, (_, _, _) =>
            Task.FromResult<object>(Interlocked.Increment(ref executions) == 1 ? first.RootElement : second.RootElement),
            logger, CancellationToken.None);
        context.Response.Body.Position = 0;
        using JsonDocument response = await JsonDocument.ParseAsync(context.Response.Body);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(executions, Is.EqualTo(2));
            Assert.That(context.Response.Body.Length, Is.LessThanOrEqualTo(RpcEndpoint.BatchResponseLimit));
            Assert.That(response.RootElement.GetArrayLength(), Is.EqualTo(4));
            Assert.That(response.RootElement[0].GetProperty("result").GetString()!.Length, Is.EqualTo(firstTokenBytes - 2));
            if (overBoundary) Assert.That(response.RootElement[1].GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(-32005));
            else Assert.That(response.RootElement[1].GetProperty("result").GetString()!.Length, Is.EqualTo(secondTokenBytes - 2));
            Assert.That(response.RootElement[2].GetProperty("id").GetString(), Is.EqualTo("retained"));
            Assert.That(response.RootElement[2].GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(-32005));
            Assert.That(response.RootElement[3].GetProperty("id").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(response.RootElement[3].GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(-32600));
            Assert.That(logger.Entries, Has.Count.EqualTo(5));
            Assert.That(logger.Entries[1], Does.Contain(overBoundary ? "error -32005" : "-> ok"));
            Assert.That(logger.Entries[3], Does.Contain("RPC notification -> error -32005 in "));
            Assert.That(logger.Entries[4], Does.Contain("RPC invalid -> error -32600 in "));
        }
    }

    [Test]
    public async Task Rpc_notifications_produce_no_response()
    {
        DefaultHttpContext context = await InvokeAsync("{\"jsonrpc\":\"2.0\",\"method\":\"eth_chainId\"}");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(204));
            Assert.That(context.Response.Body.Length, Is.Zero);
        }
    }

    [Test]
    public async Task Rpc_requests_log_method_result_and_duration()
    {
        RecordingLogger logger = new();
        await InvokeAsync("{\"jsonrpc\":\"2.0\",\"method\":\"eth_chainId\",\"id\":1}", logger);
        await InvokeAsync("{\"jsonrpc\":\"2.0\",\"method\":\"unknown\",\"id\":2}", logger);
        await InvokeAsync("{\"jsonrpc\":\"2.0\",\"method\":\"eth_chainId\"}", logger);
        await InvokeAsync("{\"jsonrpc\":\"2.0\",\"method\":\"unknown\\n\\u001bmethod\",\"id\":3}", logger);

        Assert.That(logger.Entries, Has.Count.EqualTo(4));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(logger.Entries[0], Does.Match(@"RPC eth_chainId -> ok in \d+(\.\d+)? ms"));
            Assert.That(logger.Entries[1], Does.Contain("RPC unknown -> error -32601 in "));
            Assert.That(logger.Entries[2], Does.Contain("RPC eth_chainId -> notification in "));
            Assert.That(logger.Entries[3], Does.Contain("RPC unknown  method -> error -32601 in "));
        }
    }

    [Test]
    public async Task Only_single_transaction_submission_can_exceed_the_standard_body_limit([Values] bool transaction)
    {
        string body = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = transaction ? "eth_sendRawTransaction" : "eth_chainId",
            padding = new string('x', RpcEndpoint.StandardRequestBodySize),
        });
        DefaultHttpContext context = new();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();
        using VerifiedRpc rpc = new(null!, () => throw new AssertionException("Unexpected head read."), 1);
        await RpcEndpoint.HandleAsync(context, rpc.InvokeAsync, NullLogger.Instance);
        Assert.That(context.Response.StatusCode, Is.EqualTo(transaction ? 200 : StatusCodes.Status413PayloadTooLarge));
        if (transaction)
        {
            context.Response.Body.Position = 0;
            using JsonDocument response = await JsonDocument.ParseAsync(context.Response.Body);
            Assert.That(response.RootElement.GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(-32601));
        }
    }

    [Test]
    public async Task Rpc_body_above_the_size_limit_is_refused([Values] bool rejectedByServer)
    {
        DefaultHttpContext context = new();
        context.Request.Body = rejectedByServer
            ? new OversizedBody()
            : new MemoryStream(new byte[RpcEndpoint.MaxRequestBodySize + 1]);
        context.Response.Body = new MemoryStream();
        RecordingLogger logger = new();

        using VerifiedRpc rpc = new(null!, () => throw new AssertionException("Unexpected head read."), 1);
        await RpcEndpoint.HandleAsync(context, rpc.InvokeAsync, logger);

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status413PayloadTooLarge));
        Assert.That(logger.Entries, Has.Count.EqualTo(1));
        Assert.That(logger.Entries[0], Does.Contain("body too large"));
    }

    [Test]
    [NonParallelizable]
    public async Task Rpc_endpoint_rejects_requests_above_active_limit()
    {
        TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task[] active = new Task[64];
        BlockingBody[] bodies = new BlockingBody[active.Length];
        using VerifiedRpc rpc = new(null!, () => throw new AssertionException("Unexpected head read."), 1);
        try
        {
            for (int i = 0; i < active.Length; i++)
            {
                bodies[i] = new BlockingBody(release.Task);
                DefaultHttpContext context = new();
                context.Request.Body = bodies[i];
                context.Response.Body = new MemoryStream();
                active[i] = RpcEndpoint.HandleAsync(context, rpc.InvokeAsync, NullLogger.Instance);
            }

            for (int i = 0; i < bodies.Length; i++)
                await bodies[i].Entered.WaitAsync(TimeSpan.FromSeconds(10));

            DefaultHttpContext rejected = await InvokeAsync("{\"jsonrpc\":\"2.0\",\"method\":\"eth_chainId\",\"id\":1}");
            Assert.That(rejected.Response.StatusCode, Is.EqualTo(StatusCodes.Status429TooManyRequests));
        }
        finally
        {
            release.TrySetResult(true);
            await Task.WhenAll(active.Where(static task => task is not null));
            foreach (BlockingBody body in bodies)
                body?.Dispose();
        }
    }

    [Test]
    [NonParallelizable]
    public async Task Rpc_batch_deadline_returns_gateway_timeout_and_accepts_next_request()
    {
        WaitingExecutionSource source = new();
        ManualDeadlineClock clock = new();
        using VerifiedRpc rpc = new(source, () => new VerifiedHead(1, 1, Keccak.Zero, Keccak.Zero), 1);
        DefaultHttpContext context = new();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(
            "[{\"jsonrpc\":\"2.0\",\"method\":\"eth_getBalance\",\"params\":[\"0x1234567890123456789012345678901234567890\",\"finalized\"],\"id\":1},{\"jsonrpc\":\"2.0\",\"method\":\"eth_chainId\",\"id\":2}]"));
        context.Response.Body = new MemoryStream();

        Task pending = RpcEndpoint.HandleAsync(context, rpc.InvokeAsync, NullLogger.Instance, clock);
        await source.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(clock.DueTime, Is.EqualTo(TimeSpan.FromSeconds(90)));
        clock.Expire();
        await pending.WaitAsync(TimeSpan.FromSeconds(10));
        DefaultHttpContext next = await InvokeAsync("{\"jsonrpc\":\"2.0\",\"method\":\"eth_chainId\",\"id\":3}");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status504GatewayTimeout));
            Assert.That(context.Response.Body.Length, Is.Zero);
            Assert.That(next.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
        }
    }

    [Test]
    [NonParallelizable]
    public async Task Rpc_notification_completing_after_deadline_returns_gateway_timeout()
    {
        WaitingExecutionSource source = new(ignoreCancellation: true);
        ManualDeadlineClock clock = new();
        using VerifiedRpc rpc = new(source, () => new VerifiedHead(1, 1, Keccak.Zero, Keccak.Zero), 1);
        DefaultHttpContext context = new();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(
            "{\"jsonrpc\":\"2.0\",\"method\":\"eth_getBalance\",\"params\":[\"0x1234567890123456789012345678901234567890\",\"finalized\"]}"));
        context.Response.Body = new MemoryStream();

        Task pending = RpcEndpoint.HandleAsync(context, rpc.InvokeAsync, NullLogger.Instance, clock);
        await source.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Expire();
        source.Complete();
        await pending.WaitAsync(TimeSpan.FromSeconds(10));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status504GatewayTimeout));
            Assert.That(context.Response.Body.Length, Is.Zero);
        }
    }

    private static async Task<DefaultHttpContext> InvokeAsync(string request, ILogger? logger = null)
    {
        using VerifiedRpc rpc = new(null!, () => throw new AssertionException("Unexpected head read."), 1);
        DefaultHttpContext context = new();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(request));
        context.Response.Body = new MemoryStream();
        await RpcEndpoint.HandleAsync(context, rpc.InvokeAsync, logger ?? NullLogger.Instance);
        return context;
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add(formatter(state, exception));
    }

    private sealed class OversizedBody : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge));
    }

    private sealed class BlockingBody(Task release) : Stream
    {
        private readonly MemoryStream _content = new(Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"method\":\"eth_chainId\",\"id\":1}"));
        private readonly TaskCompletionSource<bool> _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _content.Read(buffer, offset, count);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _entered.TrySetResult(true);
            await release.WaitAsync(cancellationToken);
            return await _content.ReadAsync(buffer, cancellationToken);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _content.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class WaitingExecutionSource(bool ignoreCancellation = false) : IExecutionStateSource
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<Account> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;
        public void Complete() => _result.TrySetResult(new Account(1));

        public Task<Account> GetAccountAsync(VerifiedHead head, Address address, CancellationToken cancellationToken)
        {
            _entered.TrySetResult();
            return _result.Task.WaitAsync(ignoreCancellation ? CancellationToken.None : cancellationToken);
        }

        public Task<UInt256> GetStorageAsync(VerifiedHead head, Address address, Account account, UInt256 key, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<byte[]> GetCodeAsync(Account account, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BlockHeader> GetHeaderAsync(VerifiedHead head, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Hash256[]> GetAncestorHashesAsync(VerifiedHead head, ulong firstNumber, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class ExecutionPeers : IAsyncDisposable
    {
        internal ExecutionClock Clock { get; } = new();
        internal ExecutionPeerTransport Transport { get; }
        internal ISyncPeerPool Pool { get; }
        internal ExecutionPeer[] Ordered { get; }
        internal Channel<ExecutionPeer> Started { get; } = Channel.CreateUnbounded<ExecutionPeer>();
        internal BlockHeader[] Chain { get; }
        internal BlockHeader Header => Chain[^1];
        internal VerifiedHead Head => new(2, Header.Number, Header.Hash!, Header.StateRoot!);
        internal BlockBody Body { get; }
        internal TxReceipt[] Receipts { get; } = [new() { StatusCode = 1, GasUsed = 21_000, Logs = [], Bloom = Bloom.Empty }];
        internal static readonly Address SnapAddress = new("0x1234567890123456789012345678901234567890");
        internal byte[] SnapCode { get; private set; } = null!;
        internal Account SnapAccount { get; private set; } = null!;
        internal byte[] AccountLeaf { get; private set; } = null!;
        internal byte[] StorageLeaf { get; private set; } = null!;
        internal UInt256 SnapValue { get; private set; }
        internal VerifiedHead SnapHead => new(2, Header.Number, Header.Hash!, Header.StateRoot!);
        private bool _disposed;
        private int _balRequest;

        internal ExecutionPeers(int count, bool snap2 = false, byte ethVersion = 71, bool snap1 = false,
            ulong startupTimestamp = 1_800_000_000)
        {
            Transport = new("sepolia", Nethermind.Logging.LimboLogs.Instance,
                new VerifiedHead(0, 0, KnownHashes.SepoliaGenesis, Keccak.Zero), startupTimestamp, clock: Clock);
            Body = new([new Transaction { Nonce = 1, To = Address.Zero, Value = 1, GasLimit = 21_000, GasPrice = 1 }], [], []);
            Chain = new BlockHeader[3];
            for (int i = 0; i < Chain.Length; i++)
            {
                BlockHeader header = EmptyHeader();
                header.Number = (ulong)i;
                header.Timestamp = 1_800_000_000;
                header.ParentHash = i == 0 ? Keccak.Zero : Chain[i - 1].Hash!;
                header.StateRoot = Keccak.EmptyTreeHash;
                header.TxRoot = TxTrie.CalculateRoot(Body.Transactions);
                header.ReceiptsRoot = ReceiptTrie.CalculateRoot(Transport.SpecProvider.GetSpec(header), Receipts, new ReceiptMessageDecoder());
                header.BlockAccessListHash = Keccak.Compute(new byte[] { 0xc0 });
                header.Hash = header.CalculateHash();
                Chain[i] = header;
            }
            SetSnapState(42, 123, [0x60, 0x01, 0x60, 0x00, 0x55]);
            Pool = Services(Transport).Resolve<ISyncPeerPool>();
            Pool.Start();
            ExecutionPeer[] peers = Enumerable.Range(0, count).Select(i => new ExecutionPeer(this, i + 1, snap2, ethVersion, snap1)).ToArray();
            foreach (ExecutionPeer peer in peers) Pool.AddPeer(peer.SyncPeer);
            Ordered = Pool.AllPeers.Select(info => peers.Single(peer => ReferenceEquals(peer.SyncPeer, info.SyncPeer))).ToArray();
        }

        internal void SetSnapState(UInt256 value, UInt256 balance, byte[] code)
        {
            SnapValue = value;
            SnapCode = code;
            StorageLeaf = ExecutionProofFixtures.Leaf(Keccak.Compute(((UInt256)1).ToBigEndian()), Rlp.Encode(value).Bytes);
            SnapAccount = new Account(7, balance, Keccak.Compute(StorageLeaf), Keccak.Compute(SnapCode));
            AccountLeaf = ExecutionProofFixtures.Leaf(Keccak.Compute(SnapAddress.Bytes), AccountDecoder.Instance.EncodeAsBytes(SnapAccount));
            Header.StateRoot = Keccak.Compute(AccountLeaf);
            Header.Hash = Header.CalculateHash();
        }

        internal int[] Counts(AllocationContexts context) => Ordered.Select(peer => peer.Calls.GetValueOrDefault(context)).ToArray();
        internal int BulkRequests => Counts(AllocationContexts.Bodies).Sum() + Counts(AllocationContexts.Receipts).Sum()
            + Counts(AllocationContexts.BlockAccessLists).Sum();

        internal async Task Request(AllocationContexts context, CancellationToken token)
        {
            switch (context)
            {
                case AllocationContexts.Headers:
                    Assert.That(await Transport.GetHeaderByHashAsync(Header.Hash!, token), Is.SameAs(Header));
                    break;
                case AllocationContexts.Bodies:
                    Assert.That((await Transport.GetBlockAsync(Header, token)).Transactions, Is.EqualTo(Body.Transactions));
                    break;
                case AllocationContexts.Receipts:
                    Assert.That(await Transport.GetReceiptsAsync(Header, token, Body.Transactions.Length), Has.Length.EqualTo(Receipts.Length));
                    break;
                case AllocationContexts.Snap:
                    ExecutionAccountProof proof = await Transport.GetProofAsync(SnapHead, SnapAddress, [], token);
                    Assert.That(proof.Account, Is.EqualTo(SnapAccount));
                    break;
                case AllocationContexts.BlockAccessLists:
                    Header.ExtraData = [(byte)++_balRequest];
                    Header.Hash = Header.CalculateHash();
                    Assert.That((await Transport.GetBlockAccessListAsync(Header, token)).WireHash, Is.EqualTo(Header.BlockAccessListHash));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(context));
            }
        }

        internal async Task RequestSnap(int operation, CancellationToken token)
        {
            switch (operation)
            {
                case 0:
                    Assert.That(await Transport.GetAccountAsync(SnapHead, SnapAddress, token), Is.EqualTo(SnapAccount));
                    break;
                case 1:
                    Assert.That(await Transport.GetStorageAsync(SnapHead, SnapAddress, SnapAccount, 1, token), Is.EqualTo((UInt256)42));
                    break;
                case 2:
                    Assert.That(await Transport.GetCodeAsync(SnapAccount, token), Is.EqualTo(SnapCode));
                    break;
                case 3:
                    ExecutionAccountProof proof = await Transport.GetProofAsync(SnapHead, SnapAddress, [(UInt256)1], token);
                    Assert.That(ExecutionProofVerifier.VerifyAccount(SnapHead.StateRoot, SnapAddress, proof.AccountProof), Is.EqualTo(SnapAccount));
                    Assert.That(ExecutionProofVerifier.VerifyStorage(SnapAccount.StorageRoot, 1, proof.StorageProofs.Single().Proof), Is.EqualTo((UInt256)42));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation));
            }
        }

        internal async Task RequestPayload(AllocationContexts context, int identity, CancellationToken token)
        {
            BlockHeader different = Header.Clone();
            different.ExtraData = [(byte)identity];
            different.Hash = different.CalculateHash();
            if (context == AllocationContexts.Bodies)
                Assert.That((await Transport.GetBlockAsync(different, token)).Transactions, Is.EqualTo(Body.Transactions));
            else if (context == AllocationContexts.Receipts)
                Assert.That(await Transport.GetReceiptsAsync(different, token, Body.Transactions.Length), Has.Length.EqualTo(Receipts.Length));
            else throw new ArgumentOutOfRangeException(nameof(context));
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            await Transport.DisposeAsync();
        }
    }

    private sealed class ExecutionPeer
    {
        internal ISyncPeer SyncPeer { get; } = Substitute.For<ISyncPeer>();
        internal Func<AllocationContexts, CancellationToken, Task>? BeforeResponse;
        internal ConcurrentDictionary<AllocationContexts, int> Calls { get; } = new();
        internal int ResponsesDisposed;
        internal bool InvalidData;
        internal bool InvalidAncestors;
        internal bool MissingAncestors;
        internal bool MissingReceipts;
        internal ReadOnlyMemory<int> LastReceiptCounts;
        internal ReadOnlyMemory<int>? LastReceiptLimits;

        internal ExecutionPeer(ExecutionPeers owner, int id, bool snap2, byte ethVersion, bool snap1)
        {
            using PrivateKey key = new(((UInt256)id).ToBigEndian());
            SyncPeer.Id.Returns(key.PublicKey);
            SyncPeer.Node.Returns(new Node(key.PublicKey, "127.0.0.1", 30300 + id));
            SyncPeer.ProtocolVersion.Returns(ethVersion);
            SyncPeer.IsInitialized = true;
            BlockHeader initial = EmptyHeader();
            initial.Bloom = Bloom.Empty;
            initial.Hash = initial.CalculateHash();
            // Admission must not queue a refresh that can overwrite scripted initialization.
            Services(owner.Transport).Resolve<IBlockTree>().Insert(initial, BlockTreeInsertHeaderOptions.TotalDifficultyNotNeeded);
            SyncPeer.HeadHash = initial.Hash;
            SyncPeer.GetHeadBlockHeader(Arg.Any<Hash256?>(), Arg.Any<CancellationToken>()).Returns(async call =>
            {
                if (call.ArgAt<Hash256?>(0) == initial.Hash) return (BlockHeader?)initial;
                await Read(AllocationContexts.Headers, call.ArgAt<CancellationToken>(1));
                return InvalidData ? owner.Chain[0] : owner.Header;
            });
            SyncPeer.GetBlockHeaders(Arg.Any<ulong>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(async call =>
            {
                await Read(AllocationContexts.Headers, call.ArgAt<CancellationToken>(3));
                BlockHeader[] result = owner.Chain.Skip((int)call.ArgAt<ulong>(0)).Take(call.ArgAt<int>(1)).ToArray();
                if (MissingAncestors) result = [];
                if (InvalidAncestors)
                {
                    BlockHeader broken = EmptyHeader();
                    broken.Number = 1;
                    broken.ParentHash = Keccak.Compute("wrong parent");
                    broken.Hash = broken.CalculateHash();
                    result[1] = broken;
                }
                return (IOwnedReadOnlyList<BlockHeader>?)new TrackedResponse<BlockHeader>(result, Disposed);
            });
            SyncPeer.GetBlockBodies(Arg.Any<IReadOnlyList<Hash256>>(), Arg.Any<CancellationToken>()).Returns(async call =>
            {
                await Read(AllocationContexts.Bodies, call.ArgAt<CancellationToken>(1));
                return new OwnedBlockBodies([InvalidData ? new BlockBody([], [], []) : owner.Body], new ResponseMemory(Disposed));
            });
            SyncPeer.GetReceipts(Arg.Any<IReadOnlyList<Hash256>>(), Arg.Any<ReadOnlyMemory<int>>(), Arg.Any<CancellationToken>(), Arg.Any<ReadOnlyMemory<int>?>()).Returns(async call =>
            {
                LastReceiptCounts = call.ArgAt<ReadOnlyMemory<int>>(1);
                LastReceiptLimits = call.ArgAt<ReadOnlyMemory<int>?>(3);
                await Read(AllocationContexts.Receipts, call.ArgAt<CancellationToken>(2));
                return (IOwnedReadOnlyList<TxReceipt[]?>)new TrackedResponse<TxReceipt[]?>(MissingReceipts ? [] : [InvalidData ? [] : owner.Receipts], Disposed);
            });
            SyncPeer.GetBlockAccessLists(Arg.Any<IReadOnlyList<Hash256>>(), Arg.Any<CancellationToken>()).Returns(async call =>
            {
                await Read(AllocationContexts.BlockAccessLists, call.ArgAt<CancellationToken>(1));
                return (IOwnedReadOnlyList<byte[]?>)new TrackedResponse<byte[]?>([InvalidData ? [0x80] : [0xc0]], Disposed);
            });
            if (snap1 || snap2)
            {
                ISnapSyncPeer snap = Substitute.For<ISnapSyncPeer>();
                snap.SnapProtocolVersion.Returns(snap2 ? (byte)2 : (byte)1);
                SyncPeer.TryGetSatelliteProtocol<ISnapSyncPeer>(Protocol.Snap, out Arg.Any<ISnapSyncPeer>()).Returns(call =>
                {
                    call[1] = snap;
                    return true;
                });
                snap.GetAccountRange(Arg.Any<AccountRange>(), Arg.Any<CancellationToken>()).Returns(async call =>
                {
                    await Read(AllocationContexts.Snap, call.ArgAt<CancellationToken>(1));
                    ArrayPoolList<PathWithAccount> accounts = new(1);
                    ValueHash256 path = ValueKeccak.Compute(ExecutionPeers.SnapAddress.Bytes);
                    if (call.ArgAt<AccountRange>(0).StartingHash == path)
                        accounts.Add(new(path, InvalidData ? new Account(7, 124) : owner.SnapAccount));
                    return new AccountsAndProofs
                    {
                        PathAndAccounts = accounts,
                        Proofs = new ByteArrayListAdapter(new TrackedResponse<byte[]>([owner.AccountLeaf], Disposed)),
                    };
                });
                snap.GetStorageRange(Arg.Any<StorageRange>(), Arg.Any<CancellationToken>()).Returns(async call =>
                {
                    await Read(AllocationContexts.Snap, call.ArgAt<CancellationToken>(1));
                    ArrayPoolList<PathWithStorageSlot> slots = new(1);
                    ValueHash256 path = ValueKeccak.Compute(((UInt256)1).ToBigEndian());
                    if (call.ArgAt<StorageRange>(0).StartingHash == path)
                        slots.Add(new(path, Rlp.Encode(InvalidData ? (UInt256)43 : owner.SnapValue).Bytes));
                    return new SlotsAndProofs
                    {
                        PathsAndSlots = new ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>>(1)
                        {
                            slots,
                        },
                        Proofs = new ByteArrayListAdapter(new TrackedResponse<byte[]>([owner.StorageLeaf], Disposed)),
                    };
                });
                snap.GetByteCodes(Arg.Any<IReadOnlyList<ValueHash256>>(), Arg.Any<CancellationToken>()).Returns(async call =>
                {
                    await Read(AllocationContexts.Snap, call.ArgAt<CancellationToken>(1));
                    return (IByteArrayList)new ByteArrayListAdapter(new TrackedResponse<byte[]>([InvalidData ? [0xff] : owner.SnapCode], Disposed));
                });
                snap.GetBlockAccessLists(Arg.Any<IReadOnlyList<ValueHash256>>(), Arg.Any<CancellationToken>()).Returns(async call =>
                {
                    await Read(AllocationContexts.BlockAccessLists, call.ArgAt<CancellationToken>(1));
                    return (IByteArrayList)new ByteArrayListAdapter(new TrackedResponse<byte[]>([InvalidData ? [0x80] : [0xc0]], Disposed));
                });
            }

            async Task Read(AllocationContexts context, CancellationToken token)
            {
                Calls.AddOrUpdate(context, 1, static (_, count) => count + 1);
                owner.Started.Writer.TryWrite(this);
                if (BeforeResponse is { } before) await before(context, token);
            }
        }

        private void Disposed() => Interlocked.Increment(ref ResponsesDisposed);
    }

    private sealed class TrackedResponse<T>(T[] values, Action disposed) : IOwnedReadOnlyList<T>
    {
        public int Count => values.Length;
        public T this[int index] => values[index];
        public ReadOnlySpan<T> AsSpan() => values;
        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)values).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public void Dispose() => disposed();
    }

    private sealed class ResponseMemory(Action disposed) : IMemoryOwner<byte>
    {
        public Memory<byte> Memory => Memory<byte>.Empty;
        public void Dispose() => disposed();
    }

    private sealed class ExecutionClock : TimeProvider
    {
        private readonly Lock _lock = new();
        private readonly List<ClockTimer> _timers = [];
        private long _ticks;
        internal int ActiveTimerCount { get { lock (_lock) return _timers.Count; } }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (_lock) return _ticks; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ClockTimer timer = new(this, callback, state);
            lock (_lock)
            {
                _timers.Add(timer);
                timer.Change(dueTime, period);
            }
            return timer;
        }

        internal void Advance(TimeSpan duration)
        {
            long target = GetTimestamp() + duration.Ticks;
            while (true)
            {
                ClockTimer? next;
                lock (_lock)
                {
                    next = _timers.Where(timer => timer.Due <= target).MinBy(timer => timer.Due);
                    if (next is null) { _ticks = target; return; }
                    _ticks = next.Due;
                    next.Due = long.MaxValue;
                }
                next.Fire();
            }
        }

        private sealed class ClockTimer(ExecutionClock owner, TimerCallback callback, object? state) : ITimer
        {
            internal long Due;
            internal void Fire() => callback(state);
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._lock) Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner._ticks + dueTime.Ticks;
                return true;
            }
            public void Dispose() { lock (owner._lock) owner._timers.Remove(this); }
            public ValueTask DisposeAsync() { Dispose(); return default; }
        }
    }

    private sealed class ManualDeadlineClock : TimeProvider
    {
        private TimerCallback? _callback;
        private object? _state;

        public TimeSpan DueTime { get; private set; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _callback = callback;
            _state = state;
            DueTime = dueTime;
            return new NoopTimer();
        }

        public void Expire() => _callback!(_state);

        private sealed class NoopTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => default;
        }
    }

}
