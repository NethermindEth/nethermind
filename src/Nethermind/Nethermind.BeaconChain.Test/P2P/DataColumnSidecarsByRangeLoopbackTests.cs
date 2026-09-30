// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Multiformats.Address;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.ReqResp;
using Nethermind.BeaconChain.P2P.ReqResp.Protocols;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Libp2p.Core;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.P2P;

public class DataColumnSidecarsByRangeLoopbackTests
{
    private static readonly BeaconChainSpec Spec = BeaconChainSpec.Mainnet;

    [Test]
    [CancelAfter(120_000)]
    public async Task A_host_serves_by_range_columns_of_the_canonical_block_it_stores(CancellationToken token)
    {
        const ulong slot = 13_410_304;
        const ulong column = 5;
        Hash256 root = Keccak.Compute("canonical");
        DataColumnSidecarPool serverPool = new();
        serverPool.Add(root, slot, DataColumnSidecarTestFixture.BuildValidSidecar(column, slot, blobCount: 1));
        BeaconChainStore serverStore = new(new MemColumnsDb<BeaconChainDbColumns>());
        serverStore.SetCanonicalRoot(slot, root);

        await using BeaconP2P server = CreateHost(serverStore, serverPool);
        await using BeaconP2P client = CreateHost(new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>()), new DataColumnSidecarPool());
        await server.StartAsync(token);
        await client.StartAsync(token);

        ISession toServer = await client.DialPeerAsync(LoopbackAddress(server), token);
        IReadOnlyList<DataColumnSidecar> served = await client.RequestDataColumnSidecarsByRangeAsync(toServer, slot, 1, [column], token);

        Assert.That(served.Select(static s => (s.SignedBlockHeader!.Message!.Slot, s.Index)), Is.EqualTo(new[] { (slot, column) }));
    }

    /// <summary>The host's clock must reach its by-range protocol, or it serves an incomplete range as if it were whole.</summary>
    [Test]
    [CancelAfter(120_000)]
    public async Task A_host_with_a_clock_answers_resource_unavailable_below_the_columns_it_holds_completely(CancellationToken token)
    {
        const ulong slot = 13_410_304;
        const ulong column = 5;
        Hash256 root = Keccak.Compute("canonical");
        DataColumnSidecarPool serverPool = new();
        serverPool.Add(root, slot, DataColumnSidecarTestFixture.BuildValidSidecar(column, slot, blobCount: 1));
        BeaconChainStore serverStore = new(new MemColumnsDb<BeaconChainDbColumns>());
        serverStore.SetCanonicalRoot(slot, root);
        SlotClock clock = new(Spec, new ManualTimestamper(DateTimeOffset.FromUnixTimeSeconds((long)(Spec.GenesisTime + (slot + 1) * Spec.SecondsPerSlot)).UtcDateTime));

        await using BeaconP2P server = CreateHost(serverStore, serverPool, clock);
        await using BeaconP2P client = CreateHost(new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>()), new DataColumnSidecarPool());
        await server.StartAsync(token);
        await client.StartAsync(token);

        ISession toServer = await client.DialPeerAsync(LoopbackAddress(server), token);

        Exception? error = Assert.CatchAsync(async () => await client.RequestDataColumnSidecarsByRangeAsync(toServer, slot - 1, 2, [column], token));

        Eth2ReqRespException? reqResp = error is AggregateException aggregate ? aggregate.InnerExceptions.OfType<Eth2ReqRespException>().SingleOrDefault() : error as Eth2ReqRespException;
        Assert.That(reqResp?.ResponseCode, Is.EqualTo(ReqRespFraming.ResponseCode.ResourceUnavailable));
    }

    /// <summary>
    /// A column response carries slots x columns chunks, so a per-slot budget cut a peer that was still delivering: 16 slots of 16
    /// columns need room for 256 chunks, not 16 seconds. The budget never drops below the per-slot allowance and is bounded.
    /// </summary>
    [TestCase(16UL, 1, 31)]
    [TestCase(16UL, 16, 79)]
    [TestCase(16UL, 128, 120)]
    [TestCase(1UL, 4, 31 - 15)]
    public void The_response_budget_grows_with_the_chunks_a_reply_carries(ulong slots, int columns, int expectedSeconds) =>
        Assert.That(DataColumnSidecarsByRangeProtocol.ResponseBudget(slots, columns), Is.EqualTo(TimeSpan.FromSeconds(expectedSeconds)));

    [Test]
    [CancelAfter(120_000)]
    public async Task A_reply_of_many_chunks_is_read_in_full(CancellationToken token)
    {
        const ulong firstSlot = 13_410_304;
        ulong[] columns = [0, 1, 2, 3, 4, 5, 6, 7];
        DataColumnSidecarPool serverPool = new();
        BeaconChainStore serverStore = new(new MemColumnsDb<BeaconChainDbColumns>());
        for (ulong slot = firstSlot; slot < firstSlot + 16; slot++)
        {
            Hash256 root = Keccak.Compute($"canonical {slot}");
            serverStore.SetCanonicalRoot(slot, root);
            foreach (ulong column in columns)
            {
                serverPool.Add(root, slot, DataColumnSidecarTestFixture.BuildValidSidecar(column, slot, blobCount: 1));
            }
        }

        await using BeaconP2P server = CreateHost(serverStore, serverPool);
        await using BeaconP2P client = CreateHost(new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>()), new DataColumnSidecarPool());
        await server.StartAsync(token);
        await client.StartAsync(token);

        ISession toServer = await client.DialPeerAsync(LoopbackAddress(server), token);
        IReadOnlyList<DataColumnSidecar> served = await client.RequestDataColumnSidecarsByRangeAsync(toServer, firstSlot, 16, columns, token);

        Assert.That(served, Has.Count.EqualTo(16 * columns.Length));
    }

    /// <summary>A truncated or timed-out reply used to throw away every chunk already read, so the batch asked for all of them again.</summary>
    [Test]
    public async Task A_reply_that_fails_after_some_chunks_hands_those_chunks_to_the_caller()
    {
        DataColumnSidecar delivered = DataColumnSidecarTestFixture.BuildValidSidecar(3, 5, blobCount: 1);
        ISession session = Substitute.For<ISession>();
        session.DialAsync<DataColumnSidecarsByRangeProtocol, DataColumnSidecarsDial<DataColumnSidecarsByRangeRequest>, ForkedDataColumnSidecars>(default, default).ReturnsForAnyArgs(call =>
        {
            call.Arg<DataColumnSidecarsDial<DataColumnSidecarsByRangeRequest>>().OnSidecar!(delivered);
            return Task.FromException<ForkedDataColumnSidecars>(new AggregateException(new Eth2ReqRespException("Truncated response chunk: Unable to read beyond the end of the stream.")));
        });
        await using BeaconP2P client = CreateHost(new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>()), new DataColumnSidecarPool());

        PartialSidecarsException? partial = Assert.CatchAsync<PartialSidecarsException>(async () => await client.RequestDataColumnSidecarsByRangeAsync(session, 5, 1, [3, 4], default));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(partial!.Received, Is.EqualTo(new[] { delivered }));
            Assert.That(partial.Message, Does.StartWith("Truncated response chunk"), "the failure text is the cause's, so it is classified as before");
        }
    }

    /// <summary>A stream that never opens must be cut at the fixed request timeout, not at the budget scaled for the chunks it would have carried.</summary>
    [Test]
    [CancelAfter(60_000)]
    public async Task A_reply_that_delivers_nothing_is_cut_at_the_fixed_request_timeout_whatever_the_scaled_budget(CancellationToken token)
    {
        ISession session = SessionThatDials(static async (_, ct) =>
        {
            await Task.Delay(System.Threading.Timeout.Infinite, ct);
            return new ForkedDataColumnSidecars([], []);
        });
        await using BeaconP2P client = CreateHost(new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>()), new DataColumnSidecarPool());
        Assert.That(DataColumnSidecarsByRangeProtocol.ResponseBudget(16, 8), Is.GreaterThan(TimeSpan.FromSeconds(30)));

        long startedAt = Stopwatch.GetTimestamp();
        Assert.CatchAsync<OperationCanceledException>(async () => await client.RequestDataColumnSidecarsByRangeAsync(session, 100, 16, [0, 1, 2, 3, 4, 5, 6, 7], token));

        Assert.That(Stopwatch.GetElapsedTime(startedAt), Is.LessThan(TimeSpan.FromSeconds(22)));
    }

    /// <summary>Once a peer delivers, it may take the scaled budget: 16 slots of 16 columns are cut neither at the fixed request timeout nor at one second per slot on top of it.</summary>
    [Test]
    [CancelAfter(90_000)]
    public async Task A_reply_that_keeps_delivering_past_the_fixed_request_timeout_succeeds_within_the_scaled_budget(CancellationToken token)
    {
        DataColumnSidecar first = DataColumnSidecarTestFixture.BuildValidSidecar(3, 5, blobCount: 1);
        ISession session = SessionThatDials(async (dial, ct) =>
        {
            dial.OnSidecar!(first);
            await Task.Delay(TimeSpan.FromSeconds(38), ct);
            return new ForkedDataColumnSidecars([first], []);
        });
        await using BeaconP2P client = CreateHost(new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>()), new DataColumnSidecarPool());
        Assert.That(DataColumnSidecarsByRangeProtocol.ResponseBudget(16, 16), Is.GreaterThan(TimeSpan.FromSeconds(60)));

        IReadOnlyList<DataColumnSidecar> served = await client.RequestDataColumnSidecarsByRangeAsync(session, 5, 16, [3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18], token);

        Assert.That(served, Is.EqualTo(new[] { first }));
    }

    /// <summary>A shutdown after some chunks arrived is not a peer failure: it must surface as cancellation, not as a partial reply that penalizes the peer.</summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task A_caller_cancelled_after_some_chunks_gets_cancellation_not_a_partial_reply(CancellationToken token)
    {
        using CancellationTokenSource shutdown = CancellationTokenSource.CreateLinkedTokenSource(token);
        DataColumnSidecar first = DataColumnSidecarTestFixture.BuildValidSidecar(3, 5, blobCount: 1);
        ISession session = SessionThatDials(async (dial, ct) =>
        {
            dial.OnSidecar!(first);
            await shutdown.CancelAsync();
            await Task.Delay(System.Threading.Timeout.Infinite, ct);
            return new ForkedDataColumnSidecars([], []);
        });
        await using BeaconP2P client = CreateHost(new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>()), new DataColumnSidecarPool());

        Exception? thrown = Assert.CatchAsync<Exception>(async () => await client.RequestDataColumnSidecarsByRangeAsync(session, 5, 1, [3], shutdown.Token));

        Assert.That(thrown, Is.InstanceOf<OperationCanceledException>());
    }

    /// <summary>
    /// A libp2p dial gives up on the caller's cancellation but leaves its protocol reading, so a first chunk can reach the
    /// callback after the request ended and its timeout was disposed; that must not fault the read of the reply.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    public async Task A_first_chunk_delivered_after_the_request_ended_is_dropped_without_faulting_the_reply_read(CancellationToken token)
    {
        const ulong slot = 13_410_304;
        const ulong column = 5;
        Hash256 root = Keccak.Compute("canonical");
        DataColumnSidecarPool serverPool = new();
        serverPool.Add(root, slot, DataColumnSidecarTestFixture.BuildValidSidecar(column, slot, blobCount: 1));
        BeaconChainStore serverStore = new(new MemColumnsDb<BeaconChainDbColumns>());
        serverStore.SetCanonicalRoot(slot, root);

        await using BeaconP2P server = CreateHost(serverStore, serverPool);
        await using BeaconP2P client = CreateHost(new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>()), new DataColumnSidecarPool());
        await server.StartAsync(token);
        await client.StartAsync(token);
        ISession toServer = await client.DialPeerAsync(LoopbackAddress(server), token);

        TaskCompletionSource chunkRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim requestEnded = new();
        TaskCompletionSource chunkDelivered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? deliveryFailure = null;
        ISession heldChunk = Substitute.For<ISession>();
        heldChunk.DialAsync<DataColumnSidecarsByRangeProtocol, DataColumnSidecarsDial<DataColumnSidecarsByRangeRequest>, ForkedDataColumnSidecars>(default, default).ReturnsForAnyArgs(call =>
        {
            DataColumnSidecarsDial<DataColumnSidecarsByRangeRequest> dial = call.Arg<DataColumnSidecarsDial<DataColumnSidecarsByRangeRequest>>();
            Action<DataColumnSidecar> requestCallback = dial.OnSidecar!;
            return toServer.DialAsync<DataColumnSidecarsByRangeProtocol, DataColumnSidecarsDial<DataColumnSidecarsByRangeRequest>, ForkedDataColumnSidecars>(
                dial with
                {
                    OnSidecar = sidecar =>
                    {
                        // The chunk is read while the request is live and reaches the request's callback only once it has ended.
                        chunkRead.TrySetResult();
                        requestEnded.Wait(token);
                        try
                        {
                            requestCallback(sidecar);
                        }
                        catch (Exception e)
                        {
                            deliveryFailure = e;
                        }

                        chunkDelivered.TrySetResult();
                    },
                },
                call.Arg<CancellationToken>());
        });

        using CancellationTokenSource caller = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<IReadOnlyList<DataColumnSidecar>> request = client.RequestDataColumnSidecarsByRangeAsync(heldChunk, slot, 1, [column], caller.Token);
        await chunkRead.Task.WaitAsync(token);
        await caller.CancelAsync();
        await Assert.ThatAsync(() => request, Throws.InstanceOf<OperationCanceledException>());
        requestEnded.Set();
        await chunkDelivered.Task.WaitAsync(token);

        Assert.That(deliveryFailure, Is.Null, "a chunk that arrives after the request's timeout is disposed has nothing left to extend");
    }

    private static ISession SessionThatDials(Func<DataColumnSidecarsDial<DataColumnSidecarsByRangeRequest>, CancellationToken, Task<ForkedDataColumnSidecars>> dial)
    {
        ISession session = Substitute.For<ISession>();
        session.DialAsync<DataColumnSidecarsByRangeProtocol, DataColumnSidecarsDial<DataColumnSidecarsByRangeRequest>, ForkedDataColumnSidecars>(default, default).ReturnsForAnyArgs(
            call => dial(call.Arg<DataColumnSidecarsDial<DataColumnSidecarsByRangeRequest>>(), call.Arg<CancellationToken>()));
        return session;
    }

    private static BeaconP2P CreateHost(BeaconChainStore store, DataColumnSidecarPool pool, SlotClock? clock = null) =>
        new(new BeaconChainConfig { P2PPort = 0 }, Spec, store, new BeaconChainStatusHolder(Spec, Timestamper.Default), new LocalMetadataSource(), pool, new ExecutionPayloadEnvelopePool(), LimboLogs.Instance, clock: clock);

    private static Multiaddress LoopbackAddress(BeaconP2P node)
    {
        string address = node.ListenAddresses.First().ToString().Replace("0.0.0.0", "127.0.0.1");
        if (!address.Contains("/p2p/"))
        {
            address += $"/p2p/{node.LocalPeerId}";
        }

        return Multiaddress.Decode(address);
    }
}
