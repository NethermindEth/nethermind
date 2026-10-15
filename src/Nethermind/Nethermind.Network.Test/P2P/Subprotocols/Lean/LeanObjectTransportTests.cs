// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Lean;
using NUnit.Framework;
using static Nethermind.Network.Test.P2P.Subprotocols.Lean.LeanTestObjects;

namespace Nethermind.Network.Test.P2P.Subprotocols.Lean;

public class LeanObjectTransportTests
{
    private const int ChunkBytes = LeanProtocol.ChunkBytes;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static FakeLink Link(string name, params Transaction[] transactions) => new(name)
    {
        OnRecover = hashes => transactions.Where(transaction => Array.IndexOf(hashes, transaction.Hash!.ValueHash256) >= 0).ToArray()
    };
    private static void Deliver(LeanTestNode node, LeanPeer peer, ChunkMessage chunk) =>
        node.Transport.OnChunk(peer, LeanChunkView.Parse(Wire(chunk)));
    private static void Serve(LeanTestNode node, LeanPeer peer, GetChunksMessage request, LeanDescriptor descriptor, byte[] body,
        int count = int.MaxValue, LeanCompleteStatus? complete = LeanCompleteStatus.Served)
    {
        foreach (int index in request.Indices.Take(count)) Deliver(node, peer, Chunk(request.RequestId, descriptor, body, index));
        if (complete is { } status) node.Transport.OnComplete(peer, new(request.RequestId, descriptor.ObjectId, status));
    }
    private static void ServeAll(LeanTestNode node, LeanPeer peer, FakeLink link, LeanDescriptor descriptor, byte[] body)
    {
        HashSet<ulong> served = [];
        while (link.Sent<GetChunksMessage>().FirstOrDefault(request => request.ObjectId == descriptor.ObjectId && !served.Contains(request.RequestId)) is { } request)
        {
            served.Add(request.RequestId);
            Serve(node, peer, request, descriptor, body);
        }
    }
    private static async Task Until(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(Timeout);
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    [Test]
    public async Task Wire_valid_aggregate_beyond_local_capacity_is_refused_without_peer_penalty()
    {
        using LeanTestNode node = new();
        Transaction transaction = FrameTransaction(92);
        List<FrameDependency> dependencies = Eip8288Dependencies.Canonicalize(Enumerable.Range(0, Eip8288Constants.MaxProofDependencies + 1)
            .Select(index => new FrameDependency(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute($"capacity:{index}"), default)));
        byte[] body = MempoolWrapperDecoder.Instance.Encode(new MempoolWrapper
        {
            Transactions = [new WrapperTransaction(transaction.Hash!)],
            Mode = MempoolWrapper.ModeRecursive,
            Deps = dependencies,
            RecursiveStark = new(Proof(1024), new Hash256(Eip8288Dependencies.ComputeDepsHash(dependencies)))
        }).Bytes;
        LeanDescriptor descriptor = Describe(body);
        FakeLink link = Link("local-capacity", transaction);
        LeanPeer peer = node.Connect(link);
        node.Transport.OnAnnounce(peer, new([descriptor]));
        ServeAll(node, peer, link, descriptor, body);
        await Until(() => node.Transport.AssemblyCount == 0);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(link.Penalties, Is.Empty);
            Assert.That(link.RecoveryCalls, Is.Zero);
            Assert.That(node.Verifier.Entered.Task.IsCompleted, Is.False);
            Assert.That(node.Transport.IsStored(descriptor.ObjectId), Is.False);
            Assert.That(node.Transport.IsTombstoned(descriptor.ObjectId), Is.False);
        }
    }

    [Test]
    public async Task Maximum_proof_validates_with_owned_input_and_charged_decoder_copy()
    {
        using LeanTestNode node = new();
        Transaction transaction = FrameTransaction(90);
        node.Pending.Add(transaction);
        byte[] body = Wrapper(Eip8288Constants.MaxAggregateProofBytes, true, transaction);
        LeanDescriptor descriptor = Describe(body);
        FakeLink link = Link("maximum");
        LeanPeer peer = node.Connect(link);
        Assert.That(node.Transport.TryReserveNodeBytes(link.NodeKey, 512 * 1024, out IDisposable? credit), Is.True);
        using (credit)
        using (ManualResetEventSlim gate = new())
        {
            node.Verifier.Gate = gate;
            node.Transport.OnAnnounce(peer, new([descriptor]));
            ServeAll(node, peer, link, descriptor, body);
            await node.Verifier.Entered.Task.WaitAsync(Timeout);
            try
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(node.Transport.IncompleteBytes + node.Transport.CompletionBytes,
                        Is.EqualTo(body.Length + Eip8288Constants.MaxAggregateProofBytes + 512 * 1024 + descriptor.ChunkCount * 24L + 1024));
                    Assert.That(peer.ChargedBytes, Is.LessThanOrEqualTo(LeanLimits.MaxPeerBytes));
                    Assert.That(link.Penalties, Is.Empty);
                }
            }
            finally { gate.Set(); }
            await Until(() => node.Transport.IsStored(descriptor.ObjectId));
            await Until(() => node.Transport.CompletionBytes == 0);
            Assert.That(node.Transport.IncompleteBytes, Is.EqualTo(512 * 1024));
        }
        Assert.That(node.Transport.IncompleteBytes, Is.Zero);
    }

    [Test]
    public async Task Concurrent_maximum_proofs_queue_until_decoder_capacity_is_released()
    {
        using LeanTestNode node = new();
        Transaction first = FrameTransaction(90), second = FrameTransaction(91);
        node.Pending.Add(first);
        node.Pending.Add(second);
        byte[] firstBody = Wrapper(Eip8288Constants.MaxAggregateProofBytes, true, first);
        byte[] secondBody = Wrapper(Eip8288Constants.MaxAggregateProofBytes, true, second);
        LeanDescriptor firstDescriptor = Describe(firstBody), secondDescriptor = Describe(secondBody);
        FakeLink link = Link("maximum");
        LeanPeer peer = node.Connect(link);
        Assert.That(node.Transport.TryReserveNodeBytes(link.NodeKey, 512 * 1024, out IDisposable? credit), Is.True);
        using (credit)
        using (ManualResetEventSlim gate = new())
        {
            node.Verifier.Gate = gate;
            node.Transport.OnAnnounce(peer, new([firstDescriptor, secondDescriptor]));
            ServeAll(node, peer, link, firstDescriptor, firstBody);
            await node.Verifier.Entered.Task.WaitAsync(Timeout);
            try
            {
                Assert.That(link.Sent<GetChunksMessage>().Any(request => request.ObjectId == secondDescriptor.ObjectId), Is.False);
                Assert.That(peer.ChargedBytes, Is.LessThanOrEqualTo(LeanLimits.MaxPeerBytes));
            }
            finally { gate.Set(); }
            await Until(() => link.Sent<GetChunksMessage>().Any(request => request.ObjectId == secondDescriptor.ObjectId));
            ServeAll(node, peer, link, secondDescriptor, secondBody);
            await Until(() => node.Transport.IsStored(firstDescriptor.ObjectId) && node.Transport.IsStored(secondDescriptor.ObjectId));
            await Until(() => node.Transport.AssemblyCount == 0);
            Assert.That(link.Penalties, Is.Empty);
            Assert.That(peer.ChargedBytes, Is.EqualTo(512 * 1024));
        }
    }

    [Test]
    public void Queued_metadata_does_not_start_the_body_retrieval_deadline()
    {
        using LeanTestNode node = new();
        FakeLink link = Link("queued");
        LeanPeer peer = node.Connect(link);
        Assert.That(node.Transport.TryReserveNodeBytes(link.NodeKey, 17 * 1024 * 1024, out IDisposable? credit), Is.True);
        byte[] body = OpaqueWrapperBody(2 * ChunkBytes);
        LeanDescriptor descriptor = Describe(body);
        node.Transport.OnAnnounce(peer, new([descriptor]));
        Assert.That(link.Sent<GetChunksMessage>(), Is.Empty);
        node.Tick(TimeSpan.FromSeconds(20));
        Assert.That(node.Transport.AssemblyCount, Is.EqualTo(1), "metadata waits within its separate bounded queue lifetime");
        credit!.Dispose();
        node.Tick(TimeSpan.FromMilliseconds(1));
        Assert.That(link.Sent<GetChunksMessage>(), Has.Length.EqualTo(1));
        node.Tick(TimeSpan.FromSeconds(15));
        Assert.That(node.Transport.AssemblyCount, Is.EqualTo(1), "the body idle timer starts at retrieval, not the earlier admission");
        node.Tick(LeanProtocol.MaxAssemblyIdle + TimeSpan.FromSeconds(1));
        Assert.That(node.Transport.AssemblyCount, Is.Zero);
    }

    [Test]
    public async Task Fetched_sidecar_remains_charged_until_native_validation_owner_releases_it()
    {
        using LeanTestNode node = new();
        FakeLink link = Link("source");
        LeanPeer peer = node.Connect(link);
        byte[] body = LeanBodies.EncodeBlockProof(Proof(2 * ChunkBytes));
        LeanDescriptor descriptor = LeanDescriptor.Create(LeanProtocol.KindBlockProof, LeanObjectTransport.LocalProfile,
            LeanDescriptor.BlockProofContext(Keccak.Zero.ValueHash256), body);
        using CancellationTokenSource timeout = new(Timeout);
        Task<LeanFetchedBody?> fetch = node.Transport.FetchBodyAsync(descriptor, peer, timeout.Token);
        ServeAll(node, peer, link, descriptor, body);
        LeanFetchedBody? fetched = await fetch;
        Assert.That(fetched, Is.Not.Null);
        await Until(() => node.Transport.AssemblyCount == 0);
        Assert.That(node.Transport.CompletionBytes + node.Transport.IncompleteBytes, Is.EqualTo(body.Length), "verification still owns the charged contiguous body");
        fetched!.Dispose();
        fetched.Dispose();
        Assert.That(node.Transport.CompletionBytes + node.Transport.IncompleteBytes, Is.Zero);
    }

    [Test]
    public void Retrieval_reserves_capacity_and_only_one_source_receives_body_requests()
    {
        using LeanTestNode node = new();
        FakeLink a = Link("a"), b = Link("b");
        LeanPeer peerA = node.Connect(a), peerB = node.Connect(b);
        LeanDescriptor descriptor = Describe(OpaqueWrapperBody(140 * ChunkBytes));
        node.Transport.OnAnnounce(peerA, new([descriptor]));
        node.Transport.OnAnnounce(peerB, new([descriptor]));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(a.Sent<GetChunksMessage>(), Has.Length.EqualTo(4));
            Assert.That(a.Sent<GetChunksMessage>().All(request => request.Indices.Length <= 32), Is.True);
            Assert.That(b.Sent<GetChunksMessage>(), Is.Empty);
            Assert.That(node.Transport.IncompleteBytes, Is.EqualTo(peerA.ChargedBytes));
            Assert.That(peerA.ChargedBytes, Is.LessThan(LeanLimits.MaxPeerBytes));
            Assert.That(node.Transport.AssemblyCount, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task Source_switch_discards_all_partial_bytes_and_restarts_at_zero()
    {
        using LeanTestNode node = new();
        Transaction transaction = FrameTransaction(1);
        FakeLink a = Link("a", transaction), b = Link("b", transaction);
        LeanPeer peerA = node.Connect(a), peerB = node.Connect(b);
        byte[] body = Wrapper(5 * ChunkBytes, true, transaction);
        LeanDescriptor descriptor = Describe(body);
        node.Transport.OnAnnounce(peerA, new([descriptor]));
        node.Transport.OnAnnounce(peerB, new([descriptor]));
        GetChunksMessage first = a.Sent<GetChunksMessage>().Single();
        Serve(node, peerA, first, descriptor, body, 2, LeanCompleteStatus.Unavailable);
        GetChunksMessage restarted = b.Sent<GetChunksMessage>().Single();
        Assert.That(restarted.Indices, Does.Contain(0));
        Assert.That(peerA.ChargedBytes, Is.EqualTo(descriptor.ChunkCount * 24L + 1024), "only metadata remains on the origin");
        Assert.That(peerB.ChargedBytes, Is.EqualTo(2L * body.Length), "replacement source reserves body and decoding capacity");
        ServeAll(node, peerB, b, descriptor, body);
        await Until(() => node.Transport.IsStored(descriptor.ObjectId));
        Assert.That(node.Pending.Select(tx => tx.Hash), Does.Contain(transaction.Hash));
        Assert.That(a.Penalties.Concat(b.Penalties), Is.Empty);
    }

    [Test]
    public async Task Content_hash_mismatch_penalizes_only_its_source_then_retries_from_empty()
    {
        using LeanTestNode node = new();
        Transaction transaction = FrameTransaction(2);
        FakeLink a = Link("a", transaction), b = Link("b", transaction);
        LeanPeer peerA = node.Connect(a), peerB = node.Connect(b);
        byte[] body = Wrapper(2 * ChunkBytes, true, transaction);
        LeanDescriptor descriptor = Describe(body);
        node.Transport.OnAnnounce(peerA, new([descriptor]));
        node.Transport.OnAnnounce(peerB, new([descriptor]));
        byte[] corrupted = (byte[])body.Clone();
        corrupted[^1] ^= 1;
        ServeAll(node, peerA, a, descriptor, corrupted);
        await Until(() => b.Sent<GetChunksMessage>().Length > 0);
        Assert.That(b.Sent<GetChunksMessage>()[0].Indices[0], Is.Zero);
        ServeAll(node, peerB, b, descriptor, body);
        await Until(() => node.Transport.IsStored(descriptor.ObjectId));
        Assert.That(a.Penalties, Has.Length.EqualTo(1));
        Assert.That(b.Penalties, Is.Empty);
    }

    [Test]
    public async Task Integrity_retry_keeps_waiters_and_accounting_until_validation_finishes()
    {
        using LeanTestNode node = new();
        Transaction transaction = FrameTransaction(2);
        node.Pending.Add(transaction);
        FakeLink a = Link("a"), b = Link("b");
        LeanPeer peerA = node.Connect(a), peerB = node.Connect(b);
        byte[] body = Wrapper(2 * ChunkBytes, true, transaction);
        LeanDescriptor descriptor = Describe(body);
        node.Transport.OnAnnounce(peerA, new([descriptor]));
        node.Transport.OnAnnounce(peerB, new([descriptor]));
        using CancellationTokenSource timeout = new(Timeout);
        Task<LeanFetchedBody?> fetched = node.Transport.FetchBodyAsync(descriptor, peerA, timeout.Token);
        byte[] corrupted = (byte[])body.Clone();
        corrupted[^1] ^= 1;
        ServeAll(node, peerA, a, descriptor, corrupted);
        await Until(() => b.Sent<GetChunksMessage>().Length > 0);
        Assert.That(fetched.IsCompleted, Is.False);
        Assert.That(peerA.Assemblies, Is.EqualTo(1));
        Assert.That(peerA.ChargedBytes, Is.EqualTo(descriptor.ChunkCount * 24L + 1024));
        using ManualResetEventSlim gate = new();
        node.Verifier.Gate = gate;
        ServeAll(node, peerB, b, descriptor, body);
        LeanFetchedBody? lease = await fetched.WaitAsync(Timeout);
        Assert.That(lease, Is.Not.Null);
        await node.Verifier.Entered.Task.WaitAsync(Timeout);
        try
        {
            lease!.Dispose();
            Assert.That(node.Transport.CompletionBytes, Is.EqualTo(body.Length + 2 * ChunkBytes));
            Assert.That(peerB.ChargedBytes, Is.EqualTo(node.Transport.CompletionBytes));
            Assert.That(peerA.Assemblies, Is.EqualTo(1));
        }
        finally { gate.Set(); }
        await Until(() => node.Transport.IsStored(descriptor.ObjectId) && node.Transport.AssemblyCount == 0);
        Assert.That(peerA.Assemblies, Is.Zero);
        Assert.That(node.Transport.CompletionBytes + node.Transport.IncompleteBytes, Is.Zero);
    }

    [Test]
    public void Download_attempts_are_bounded_across_restarts_without_resetting_absolute_age()
    {
        using LeanTestNode node = new();
        FakeLink link = Link("source");
        LeanPeer peer = node.Connect(link);
        LeanDescriptor descriptor = Describe(OpaqueWrapperBody(2 * ChunkBytes));
        for (int attempt = 0; attempt < 4; attempt++)
        {
            int before = link.Sent<GetChunksMessage>().Length;
            node.Transport.OnAnnounce(peer, new([descriptor]));
            if (attempt == 3) { Assert.That(link.Sent<GetChunksMessage>(), Has.Length.EqualTo(before)); break; }
            GetChunksMessage request = link.Sent<GetChunksMessage>().Last();
            node.Transport.OnComplete(peer, new(request.RequestId, descriptor.ObjectId, LeanCompleteStatus.Unavailable));
        }
        Assert.That(link.Sent<GetChunksMessage>(), Has.Length.EqualTo(3));
        node.Tick(LeanProtocol.MaxAssemblyIdle);
        Assert.That(node.Transport.AssemblyCount, Is.Zero);
        Assert.That(node.Transport.IncompleteBytes, Is.Zero);
    }

    [Test]
    public void First_credited_chunk_uses_reserved_capacity_with_receive_credit_headroom()
    {
        using LeanTestNode node = new();
        FakeLink link = Link("maximum");
        LeanPeer peer = node.Connect(link);
        Assert.That(node.Transport.TryReserveNodeBytes(link.NodeKey, 512 * 1024, out IDisposable? credit), Is.True);
        using (credit)
        {
            byte[] body = Wrapper(Eip8288Constants.MaxAggregateProofBytes, true, FrameTransaction(90));
            LeanDescriptor descriptor = Describe(body);
            node.Transport.OnAnnounce(peer, new([descriptor]));
            long reserved = node.Transport.IncompleteBytes;
            GetChunksMessage request = link.Sent<GetChunksMessage>()[0];
            Deliver(node, peer, Chunk(request.RequestId, descriptor, body, 0));
            Assert.That(node.Transport.IncompleteBytes, Is.EqualTo(reserved));
            Assert.That(peer.ChargedBytes, Is.LessThan(LeanLimits.MaxPeerBytes));
            node.Tick(LeanProtocol.MaxAssemblyIdle);
            Assert.That(node.Transport.IncompleteBytes, Is.EqualTo(512 * 1024));
        }
        Assert.That(node.Transport.IncompleteBytes, Is.Zero);
    }

    [Test]
    public void Reconnecting_source_keeps_charged_node_budget_until_retained_work_releases()
    {
        using LeanTestNode node = new();
        FakeLink old = Link("identity");
        LeanPeer first = node.Connect(old);
        Assert.That(node.Transport.TryReserveNodeBytes(old.NodeKey, 17 * 1024 * 1024, out IDisposable? retained), Is.True);
        node.Transport.Remove(first);
        LeanPeer next = node.Connect(Link("identity"));
        using (retained)
        {
            Assert.That(ReferenceEquals(first.Node, next.Node), Is.True);
            Assert.That(node.Transport.TryReserveNodeBytes(old.NodeKey, 2 * 1024 * 1024, out _), Is.False);
        }
        Assert.That(next.ChargedBytes, Is.Zero);
    }

    [Test]
    public void Global_budget_is_shared_across_sources_and_refuses_before_allocation()
    {
        using LeanTestNode node = new();
        List<IDisposable> leases = [];
        try
        {
            for (int i = 0; i < 4; i++)
            {
                FakeLink link = Link($"budget:{i}");
                Assert.That(node.Transport.TryReserveNodeBytes(link.NodeKey, 18 * 1024 * 1024, out IDisposable? lease), Is.True);
                leases.Add(lease!);
            }
            Assert.That(node.Transport.TryReserveNodeBytes(Link("extra").NodeKey, 1, out _), Is.False);
            Assert.That(node.Transport.IncompleteBytes, Is.EqualTo(LeanLimits.MaxIncompleteBytes));
        }
        finally { foreach (IDisposable lease in leases) lease.Dispose(); }
        Assert.That(node.Transport.IncompleteBytes, Is.Zero);
    }

    [Test]
    public void New_retrieval_preserves_ingress_headroom([Values] bool global)
    {
        using LeanTestNode node = new();
        FakeLink link = Link("source");
        LeanPeer peer = node.Connect(link);
        byte[] body = OpaqueWrapperBody(2 * 1024 * 1024);
        LeanDescriptor descriptor = Describe(body);
        long retained = (global ? LeanLimits.MaxIncompleteBytes : LeanLimits.MaxPeerBytes) - 2L * body.Length - (global ? 1024 : 384) * 1024;
        List<IDisposable> leases = [];
        try
        {
            for (int i = 0; retained > 0; i++)
            {
                long bytes = Math.Min(retained, LeanLimits.MaxPeerBytes);
                Assert.That(node.Transport.TryReserveNodeBytes(global ? Link($"budget:{i}").NodeKey : link.NodeKey, bytes,
                    out IDisposable? lease), Is.True);
                leases.Add(lease!);
                retained -= bytes;
            }
            node.Transport.OnAnnounce(peer, new([descriptor]));
            Assert.That(link.Sent<GetChunksMessage>(), Is.Empty);
        }
        finally { foreach (IDisposable lease in leases) lease.Dispose(); }
        node.Tick(TimeSpan.FromMilliseconds(1));
        Assert.That(link.Sent<GetChunksMessage>(), Is.Not.Empty);
    }

    [Test]
    public async Task Slow_gossip_reservations_leave_room_for_a_maximum_waited_block_proof()
    {
        using LeanTestNode node = new();
        List<FakeLink> gossip = [];
        for (int index = 0; index < 4; index++)
        {
            FakeLink link = Link($"slow-gossip:{index}");
            gossip.Add(link);
            LeanPeer peer = node.Connect(link);
            byte[] body = Wrapper(Eip8288Constants.MaxAggregateProofBytes, true, FrameTransaction(120 + index));
            node.Transport.OnAnnounce(peer, new([Describe(body)]));
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(gossip.Take(3).All(link => link.Sent<GetChunksMessage>().Length > 0), Is.True);
            Assert.That(gossip[3].Sent<GetChunksMessage>(), Is.Empty, "the fourth wrapper would consume import headroom");
        }

        FakeLink healthy = Link("healthy-import");
        LeanPeer source = node.Connect(healthy);
        byte[] proofBody = LeanBodies.EncodeBlockProof(Proof(Eip8288Constants.MaxAggregateProofBytes));
        LeanDescriptor descriptor = LeanDescriptor.Create(LeanProtocol.KindBlockProof, LeanObjectTransport.LocalProfile,
            LeanDescriptor.BlockProofContext(TestItem.KeccakA.ValueHash256), proofBody);
        using CancellationTokenSource cancellation = new();
        Task<LeanFetchedBody?> fetched = node.Transport.FetchBodyAsync(descriptor, source, cancellation.Token);
        try
        {
            Assert.That(healthy.Sent<GetChunksMessage>(), Is.Not.Empty, "a waited proof can use the import reservation");
            Assert.That(healthy.Sent<GetChunksMessage>().All(request => request.ObjectId == descriptor.ObjectId), Is.True);
            Assert.That(node.Transport.IncompleteBytes, Is.LessThanOrEqualTo(LeanLimits.MaxIncompleteBytes));
        }
        finally { await cancellation.CancelAsync(); }
        Assert.That(await fetched, Is.Null);
        Assert.That(source.ChargedBytes, Is.Zero);
    }

    [Test]
    public async Task ETH_recovery_and_gossip_metadata_preserve_waited_import_capacity()
    {
        using LeanTestNode node = new();
        using ManualResetEventSlim resumeRecovery = new();
        TaskCompletionSource recovering = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<IDisposable> pressure = [];
        Transaction transaction = FrameTransaction(135);
        FakeLink gossip = Link("recovering-gossip", transaction);
        LeanPeer peer = node.Connect(gossip);
        bool firstRecovery = true;
        bool recoveryResumed = false;
        gossip.OnRecover = _ =>
        {
            if (firstRecovery)
            {
                firstRecovery = false;
                long target = LeanLimits.MaxIncompleteBytes - (long)LeanLimits.MaxObjectBytes - LeanProtocol.MaxCreditBytes - 1;
                ReserveGlobalBytes(node, target - node.Transport.IncompleteBytes - node.Transport.CompletionBytes, pressure);
                recovering.TrySetResult();
                recoveryResumed = resumeRecovery.Wait(Timeout);
            }
            return [transaction];
        };
        byte[] body = Wrapper(ChunkBytes, true, transaction);
        LeanDescriptor descriptor = Describe(body);
        node.Transport.OnAnnounce(peer, new([descriptor]));
        ServeAll(node, peer, gossip, descriptor, body);
        try
        {
            await recovering.Task.WaitAsync(Timeout);
            int assemblies = node.Transport.AssemblyCount;
            FakeLink deferred = Link("metadata-gossip");
            LeanPeer deferredSource = node.Connect(deferred);
            node.Transport.OnAnnounce(deferredSource, new([Describe(Wrapper(ChunkBytes, true, FrameTransaction(136)))]));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(node.Transport.AssemblyCount, Is.EqualTo(assemblies), "gossip metadata cannot consume the import margin");
                Assert.That(deferred.Sent<GetChunksMessage>(), Is.Empty);
            }
            node.Transport.Remove(deferredSource);

            FakeLink healthy = Link("recovery-import");
            LeanPeer source = node.Connect(healthy);
            byte[] proofBody = LeanBodies.EncodeBlockProof(Proof(Eip8288Constants.MaxAggregateProofBytes));
            LeanDescriptor proofDescriptor = LeanDescriptor.Create(LeanProtocol.KindBlockProof, LeanObjectTransport.LocalProfile,
                LeanDescriptor.BlockProofContext(TestItem.KeccakA.ValueHash256), proofBody);
            using CancellationTokenSource cancellation = new(Timeout);
            Task<LeanFetchedBody?> fetching = node.Transport.FetchBodyAsync(proofDescriptor, source, cancellation.Token);
            Assert.That(healthy.Sent<GetChunksMessage>(), Is.Not.Empty, "waited metadata and body use the import margin");
            ServeAll(node, source, healthy, proofDescriptor, proofBody);
            using LeanFetchedBody? fetched = await fetching.WaitAsync(Timeout);
            Assert.That(fetched?.Body, Is.EqualTo(proofBody));
            resumeRecovery.Set();
            await Until(() => node.Transport.AssemblyCount == 0);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(gossip.RecoveryCalls, Is.EqualTo(1));
                Assert.That(recoveryResumed, Is.True, "recovery stayed in flight until the waited proof completed");
                Assert.That(node.Pending, Is.Empty, "the ETH envelope could not reserve the protected margin");
                Assert.That(node.Transport.IsStored(descriptor.ObjectId), Is.False);
                Assert.That(gossip.Penalties, Is.Empty, "local recovery pressure is not invalid peer data");
                Assert.That(healthy.Penalties, Is.Empty);
            }
        }
        finally
        {
            resumeRecovery.Set();
            foreach (IDisposable lease in pressure) lease.Dispose();
        }
        node.Tick(LeanLimits.SoftTombstone + TimeSpan.FromSeconds(1));
        gossip.Clear();
        node.Transport.OnAnnounce(peer, new([descriptor]));
        ServeAll(node, peer, gossip, descriptor, body);
        await Until(() => node.Transport.IsStored(descriptor.ObjectId));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(gossip.RecoveryCalls, Is.EqualTo(2));
            Assert.That(node.Pending.Any(pending => pending.Hash == transaction.Hash), Is.True);
            Assert.That(gossip.Penalties, Is.Empty);
        }
    }

    private static void ReserveGlobalBytes(LeanTestNode node, long bytes, List<IDisposable> leases)
    {
        for (int index = 0; bytes > 0; index++)
        {
            long part = Math.Min(bytes, LeanLimits.MaxPeerBytes);
            Assert.That(node.Transport.TryReserveNodeBytes(Link($"global-pressure:{index}").NodeKey, part, out IDisposable? lease), Is.True);
            leases.Add(lease!);
            bytes -= part;
        }
    }

    [Test]
    public void Request_ids_allow_gaps_but_reuse_and_decrease_are_violations([Values(0UL, 7UL, 3UL)] ulong invalid)
    {
        using LeanTestNode node = new();
        FakeLink link = Link("ids");
        LeanPeer peer = node.Connect(link);
        node.Transport.OnGetChunks(peer, new(7, default, [0]));
        Assert.That(link.Sent<CompleteMessage>().Single().Status, Is.EqualTo(LeanCompleteStatus.Unavailable));
        node.Transport.OnGetChunks(peer, new(invalid, default, [0]));
        Assert.That(link.Penalties, Has.Length.EqualTo(1));
    }

    [Test]
    public void Response_for_an_unused_gap_is_discarded_but_future_id_is_invalid()
    {
        using LeanTestNode node = new();
        FakeLink link = Link("gaps");
        LeanPeer peer = node.Connect(link);
        peer.LastIssued = 10;
        node.Transport.OnObjects(peer, new(5, [LeanObjectResult.Of(LeanResultStatus.Unavailable)]));
        Assert.That(link.Penalties, Is.Empty);
        node.Transport.OnObjects(peer, new(11, [LeanObjectResult.Of(LeanResultStatus.Unavailable)]));
        Assert.That(link.Penalties, Has.Length.EqualTo(1));
    }

    [Test]
    public async Task Cancelled_request_keeps_slot_and_crossed_served_releases_it_without_body_retention()
    {
        using LeanTestNode node = new();
        FakeLink link = Link("cancel");
        LeanPeer peer = node.Connect(link);
        byte[] body = OpaqueWrapperBody(2 * ChunkBytes);
        LeanDescriptor descriptor = Describe(body);
        using CancellationTokenSource cancellation = new();
        Task<LeanFetchedBody?> fetch = node.Transport.FetchBodyAsync(descriptor, peer, cancellation.Token);
        GetChunksMessage request = link.Sent<GetChunksMessage>().Single();
        cancellation.Cancel();
        Assert.That(await fetch.WaitAsync(Timeout), Is.Null);
        Assert.That(peer.Live, Has.Count.EqualTo(1));
        Deliver(node, peer, Chunk(request.RequestId, descriptor, body, 0));
        node.Transport.OnComplete(peer, new(request.RequestId, descriptor.ObjectId, LeanCompleteStatus.Served));
        Assert.That(peer.Live, Is.Empty);
        Assert.That(node.Transport.IncompleteBytes, Is.Zero);
        Assert.That(link.Penalties, Is.Empty);
    }

    [Test]
    public async Task Cancelled_metadata_response_releases_slot_without_using_results()
    {
        using LeanTestNode node = new();
        FakeLink link = Link("metadata-cancel");
        LeanPeer peer = node.Connect(link);
        using CancellationTokenSource cancellation = new();
        Task<LeanObjectResult[]?> fetch = node.Transport.RequestObjectsAsync(peer, [new(1, LeanObjectTransport.LocalProfile, default)], cancellation.Token);
        cancellation.Cancel();
        Assert.That(await fetch.WaitAsync(Timeout), Is.Null);
        Assert.That(peer.Live, Has.Count.EqualTo(1));
        ulong id = link.Sent<GetObjectsMessage>().Single().RequestId;
        node.Transport.OnObjects(peer, new(id, [LeanObjectResult.Of(LeanResultStatus.Busy)]));
        Assert.That(peer.Live, Is.Empty);
        Assert.That(link.Penalties, Is.Empty);
    }

    [TestCase("object")]
    [TestCase("index")]
    [TestCase("length")]
    [TestCase("served early")]
    [TestCase("wrong response")]
    public void Invalid_chunk_response_is_a_protocol_violation(string invalid)
    {
        using LeanTestNode node = new();
        FakeLink link = Link("invalid");
        LeanPeer peer = node.Connect(link);
        byte[] body = OpaqueWrapperBody(2 * ChunkBytes);
        LeanDescriptor descriptor = Describe(body);
        node.Transport.OnAnnounce(peer, new([descriptor]));
        GetChunksMessage request = link.Sent<GetChunksMessage>().Single();
        switch (invalid)
        {
            case "object": Deliver(node, peer, new(request.RequestId, TestItem.KeccakA.ValueHash256, 0, body.AsMemory(0, ChunkBytes))); break;
            case "index": Deliver(node, peer, Chunk(request.RequestId, descriptor, body, 1)); break;
            case "length": Deliver(node, peer, new(request.RequestId, descriptor.ObjectId, 0, new byte[] { 1 })); break;
            case "served early": node.Transport.OnComplete(peer, new(request.RequestId, descriptor.ObjectId, LeanCompleteStatus.Served)); break;
            case "wrong response": node.Transport.OnObjects(peer, new(request.RequestId, [LeanObjectResult.Of(LeanResultStatus.Busy)])); break;
        }
        Assert.That(link.Penalties, Has.Length.EqualTo(1));
    }

    [Test]
    public async Task Invalid_proof_is_rejected_before_ETH_envelope_recovery()
    {
        using LeanTestNode node = new();
        node.Verifier.Valid = false;
        Transaction transaction = FrameTransaction(8);
        FakeLink link = Link("invalid-proof", transaction);
        LeanPeer peer = node.Connect(link);
        byte[] body = Wrapper(ChunkBytes, true, transaction);
        LeanDescriptor descriptor = Describe(body);
        node.Transport.OnAnnounce(peer, new([descriptor]));
        ServeAll(node, peer, link, descriptor, body);
        await Until(() => node.Transport.IsTombstoned(descriptor.ObjectId));
        Assert.That(link.RecoveryCalls, Is.Zero);
        Assert.That(link.Penalties, Has.Length.EqualTo(1));
    }

    [Test]
    public async Task ETH_recovered_envelopes_are_checked_against_wrapper_dependencies_before_admission()
    {
        using LeanTestNode node = new();
        Transaction transaction = FrameTransaction(9);
        FakeLink link = Link("coverage", transaction);
        LeanPeer peer = node.Connect(link);
        List<FrameDependency> claimed = [Dependency(99)];
        byte[] body = MempoolWrapperDecoder.Instance.Encode(new MempoolWrapper
        {
            Transactions = [new WrapperTransaction(transaction.Hash!)],
            Deps = claimed,
            Mode = MempoolWrapper.ModeRecursive,
            RecursiveStark = new(Proof(ChunkBytes), new(Eip8288Dependencies.ComputeDepsHash(claimed)))
        }).Bytes;
        LeanDescriptor descriptor = Describe(body);
        node.Transport.OnAnnounce(peer, new([descriptor]));
        ServeAll(node, peer, link, descriptor, body);
        await Until(() => node.Transport.IsTombstoned(descriptor.ObjectId));
        Assert.That(link.RecoveryCalls, Is.EqualTo(1));
        Assert.That(node.Pending, Is.Empty);
        Assert.That(link.Penalties, Is.Not.Empty);
    }

    [Test]
    public async Task Valid_proof_with_pool_rejection_is_retained_with_its_ETH_envelope()
    {
        using LeanTestNode node = new();
        node.Reject = true;
        Transaction transaction = FrameTransaction(10);
        FakeLink link = Link("policy", transaction);
        LeanPeer peer = node.Connect(link);
        byte[] body = Wrapper(ChunkBytes, true, transaction);
        LeanDescriptor descriptor = Describe(body);
        node.Transport.OnAnnounce(peer, new([descriptor]));
        ServeAll(node, peer, link, descriptor, body);
        await Until(() => node.Transport.IsStored(descriptor.ObjectId));
        Assert.That(node.Pending, Is.Empty);
        Assert.That(node.EthTransactions.TryGetRetained(transaction.Hash!.ValueHash256, out Transaction? retained), Is.True);
        Assert.That(Envelope(retained!), Is.EqualTo(Envelope(transaction)));
        Assert.That(link.Penalties, Is.Empty);
    }

    [Test]
    public void Missing_compatible_ETH_disables_only_lean()
    {
        using LeanTestNode node = new();
        FakeLink link = Link("no-eth");
        link.HasTransactionRecovery = false;
        Assert.That(node.Transport.Accept(link, LeanTestNode.Status()), Is.Null);
        Assert.That(link.Penalties, Is.Empty);
    }

    [Test]
    public async Task Serving_yields_and_cancel_during_write_sends_one_terminal_response()
    {
        using LeanTestNode node = new();
        Transaction transaction = FrameTransaction(11);
        node.Pending.Add(transaction);
        byte[] wrapper = Wrapper(ChunkBytes * 2, true, transaction);
        Assert.That(node.Transport.PublishWrapper(wrapper), Is.True);
        LeanDescriptor descriptor = Describe(wrapper);
        FakeLink link = Link("slow-peer");
        LeanPeer peer = node.Connect(link);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        link.OnChunk = async (_, _) => { entered.TrySetResult(); await release.Task; return true; };
        node.Transport.OnGetChunks(peer, new(1, descriptor.ObjectId, [0, 1]));
        await entered.Task.WaitAsync(Timeout);
        node.Transport.OnCancel(peer, new(1));
        release.TrySetResult();
        await Until(() => link.Sent<CompleteMessage>().Length == 1);
        Assert.That(link.Sent<CompleteMessage>().Single().Status, Is.EqualTo(LeanCompleteStatus.Cancelled));
        Assert.That(link.Penalties, Is.Empty);
    }

    [Test]
    public async Task Requests_leave_in_ID_order_when_the_first_sender_is_blocked()
    {
        using LeanTestNode node = new();
        FakeLink link = Link("ordered");
        LeanPeer peer = node.Connect(link);
        using ManualResetEventSlim release = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        link.OnSend = message =>
        {
            if (message is GetObjectsMessage { RequestId: 1 }) { entered.TrySetResult(); release.Wait(TimeSpan.FromSeconds(5)); }
        };
        Task first = Task.Run(() => node.Transport.RequestObjectsAsync(peer, [new(1, LeanObjectTransport.LocalProfile, default)], CancellationToken.None));
        await entered.Task.WaitAsync(Timeout);
        _ = node.Transport.RequestObjectsAsync(peer, [new(1, LeanObjectTransport.LocalProfile, TestItem.KeccakA.ValueHash256)], CancellationToken.None);
        release.Set();
        await Until(() => link.Sent<GetObjectsMessage>().Length == 2);
        Assert.That(link.Sent<GetObjectsMessage>().Select(request => request.RequestId), Is.EqualTo(new ulong[] { 1, 2 }));
        foreach (GetObjectsMessage request in link.Sent<GetObjectsMessage>())
            node.Transport.OnObjects(peer, new(request.RequestId, [LeanObjectResult.Of(LeanResultStatus.Unavailable)]));
        await first.WaitAsync(Timeout);
    }

    [Test]
    public void Deferred_announcements_get_freed_capacity_without_another_announcement()
    {
        using LeanTestNode node = new();
        FakeLink link = Link("deferred");
        LeanPeer peer = node.Connect(link);
        LeanDescriptor[] descriptors = Enumerable.Range(0, LeanLimits.MaxAssembliesPerPeer + 1)
            .Select(seed => Describe(OpaqueWrapperBody(ChunkBytes * 2, seed))).OrderBy(descriptor => descriptor.ObjectId).ToArray();
        node.Transport.OnAnnounce(peer, new(descriptors));
        Assert.That(peer.Deferred, Has.Count.EqualTo(1));
        node.Tick(LeanProtocol.MaxAssemblyIdle);
        Assert.That(peer.Deferred, Is.Empty);
        Assert.That(link.Penalties, Is.Empty);
    }
}
