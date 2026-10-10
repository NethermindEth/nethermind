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
using Nethermind.Specs.Forks;
using NUnit.Framework;
using static Nethermind.Network.Test.P2P.Subprotocols.Lean.LeanTestObjects;

namespace Nethermind.Network.Test.P2P.Subprotocols.Lean;

/// <summary>EIP-8437 request lifecycle, bounded reassembly, recovery and peer penalties, driven message by message.</summary>
public class LeanObjectTransportTests
{
    private const int ChunkBytes = LeanProtocol.ChunkBytes;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static void Deliver(LeanTestNode node, LeanPeer peer, ChunkMessage chunk) =>
        node.Transport.OnChunk(peer, LeanChunkView.Parse(Wire(chunk)));

    private static void Serve(LeanTestNode node, LeanPeer peer, GetChunksMessage request, LeanDescriptor descriptor, byte[] body,
        LeanChunkTree tree, int count = int.MaxValue, LeanCompleteStatus? complete = LeanCompleteStatus.Served)
    {
        foreach (int index in request.Indices.Take(count)) Deliver(node, peer, Chunk(request.RequestId, descriptor, body, tree, index));
        if (complete is { } status) node.Transport.OnComplete(peer, new CompleteMessage(request.RequestId, descriptor.ObjectId, status));
    }

    /// <summary>Serves every chunk request a peer has received and not yet answered, until it stops asking.</summary>
    private static void ServeAll(LeanTestNode node, LeanPeer peer, FakeLink link, LeanDescriptor descriptor, byte[] body, LeanChunkTree tree,
        HashSet<ulong> served)
    {
        while (link.Sent<GetChunksMessage>().FirstOrDefault(r => !served.Contains(r.RequestId)) is { } request)
        {
            served.Add(request.RequestId);
            Serve(node, peer, request, descriptor, body, tree);
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(Timeout);
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    [Test]
    public void Announcements_request_bounded_disjoint_ranges_and_allocate_only_bookkeeping()
    {
        using LeanTestNode node = new();
        FakeLink a = new("a"), b = new("b");
        LeanPeer peerA = node.Connect(a), peerB = node.Connect(b);
        byte[] body = OpaqueWrapperBody(200 * ChunkBytes);
        LeanDescriptor descriptor = Describe(body, out _);

        node.Transport.OnAnnounce(peerA, new AnnounceObjectsMessage([descriptor]));
        long charged = node.Transport.IncompleteBytes;
        node.Transport.OnAnnounce(peerB, new AnnounceObjectsMessage([descriptor]));

        GetChunksMessage[] fromA = a.Sent<GetChunksMessage>(), fromB = b.Sent<GetChunksMessage>();
        int[] indices = [.. fromA.Concat(fromB).SelectMany(r => r.Indices)];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(fromA.Select(r => r.RequestId), Is.EqualTo(new ulong[] { 1, 2, 3, 4 }), "at most MAX_REQUESTS_PER_PEER, numbered from one");
            Assert.That(fromB.Select(r => r.RequestId), Is.EqualTo(Enumerable.Range(1, fromB.Length).Select(i => (ulong)i)), "per-connection numbering");
            Assert.That(fromA.Concat(fromB).All(r => r.Indices.Length <= LeanProtocol.MaxChunksPerRequest && r.Indices.SequenceEqual(r.Indices.Order())), Is.True);
            Assert.That(indices, Is.EquivalentTo(Enumerable.Range(0, descriptor.ChunkCount)), "disjoint ranges cover every chunk once");
            Assert.That(fromB, Is.Not.Empty, "the second source takes the indices the first could not");
            Assert.That(charged, Is.LessThan(64 * 1024), "a declared length does not allocate that length");
            Assert.That(node.Transport.AssemblyCount, Is.EqualTo(1), "a known descriptor does not create another assembly");
        }
    }

    [Test]
    public async Task Interrupted_transfer_resumes_only_missing_indices_from_another_peer()
    {
        using LeanTestNode node = new();
        FakeLink a = new("a"), b = new("b");
        LeanPeer peerA = node.Connect(a), peerB = node.Connect(b);
        Transaction transaction = FrameTransaction(1);
        byte[] body = Wrapper(5 * ChunkBytes, false, transaction);
        LeanDescriptor descriptor = Describe(body, out LeanChunkTree tree);

        node.Transport.OnAnnounce(peerA, new AnnounceObjectsMessage([descriptor]));
        GetChunksMessage first = a.Sent<GetChunksMessage>().Single();
        Serve(node, peerA, first, descriptor, body, tree, count: 2, LeanCompleteStatus.Busy);
        node.Transport.OnAnnounce(peerB, new AnnounceObjectsMessage([descriptor]));
        GetChunksMessage resumed = b.Sent<GetChunksMessage>().Single();
        Serve(node, peerB, resumed, descriptor, body, tree);

        await Until(() => node.Transport.IsStored(descriptor.ObjectId));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(resumed.Indices, Is.EqualTo(first.Indices.Skip(2)));
            Assert.That(node.Pending.Select(t => t.Hash), Does.Contain(transaction.Hash));
            Assert.That(a.Penalties.Concat(b.Penalties), Is.Empty, "Busy is not misbehaviour");
            Assert.That(node.Transport.IncompleteBytes, Is.Zero);
        }
    }

    [Test]
    public async Task Corrupt_chunks_from_one_peer_do_not_discard_valid_chunks_from_others()
    {
        using LeanTestNode node = new();
        FakeLink a = new("a"), b = new("b");
        LeanPeer peerA = node.Connect(a), peerB = node.Connect(b);
        byte[] body = Wrapper(Eip8288Constants.MaxProofBytes - ChunkBytes, false, FrameTransaction(2));
        LeanDescriptor descriptor = Describe(body, out LeanChunkTree tree);
        node.Transport.OnAnnounce(peerA, new AnnounceObjectsMessage([descriptor]));
        node.Transport.OnAnnounce(peerB, new AnnounceObjectsMessage([descriptor]));

        GetChunksMessage fromA = a.Sent<GetChunksMessage>()[0];
        Serve(node, peerA, fromA, descriptor, body, tree, count: 10, complete: null);
        byte[] tampered = body.ToArray();
        tampered[fromA.Indices[10] * ChunkBytes] ^= 1;
        Deliver(node, peerA, Chunk(fromA.RequestId, descriptor, tampered, tree, fromA.Indices[10]));
        ServeAll(node, peerB, b, descriptor, body, tree, []);

        await Until(() => node.Transport.IsStored(descriptor.ObjectId));
        int[] requestedFromB = [.. b.Sent<GetChunksMessage>().SelectMany(r => r.Indices)];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(a.Penalties, Has.Length.EqualTo(1));
            Assert.That(b.Penalties, Is.Empty);
            Assert.That(requestedFromB, Has.No.AnyOf(fromA.Indices.Take(10).Cast<object>().ToArray()), "chunks verified before the corruption are kept");
            Assert.That(node.Transport.PeerCount, Is.EqualTo(1));
        }
    }

    [Test]
    public void Idle_assembly_expires_on_its_timer_without_incoming_messages()
    {
        using LeanTestNode node = new();
        FakeLink a = new();
        LeanPeer peer = node.Connect(a);
        LeanDescriptor descriptor = Describe(OpaqueWrapperBody(3 * ChunkBytes), out _);
        node.Transport.OnAnnounce(peer, new AnnounceObjectsMessage([descriptor]));
        node.Tick(LeanProtocol.MaxAssemblyIdle - TimeSpan.FromSeconds(1));
        Assert.That(node.Transport.AssemblyCount, Is.EqualTo(1));

        node.Tick(TimeSpan.FromSeconds(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.Transport.AssemblyCount, Is.Zero);
            Assert.That(node.Transport.IncompleteBytes, Is.Zero);
            Assert.That(a.Sent<CancelMessage>().Select(c => c.RequestId), Is.EqualTo(a.Sent<GetChunksMessage>().Select(r => r.RequestId)));
            Assert.That(a.Penalties, Is.Empty, "a stall is not misbehaviour");
        }
    }

    [Test]
    public void Absolute_deadline_expires_an_assembly_despite_progress_and_new_sources()
    {
        using LeanTestNode node = new();
        FakeLink a = new("a"), c = new("c");
        LeanPeer peerA = node.Connect(a), peerC = node.Connect(c);
        byte[] body = OpaqueWrapperBody(200 * ChunkBytes);
        LeanDescriptor descriptor = Describe(body, out LeanChunkTree tree);
        node.Transport.OnAnnounce(peerA, new AnnounceObjectsMessage([descriptor]));
        Dictionary<ulong, int> delivered = [];
        TimeSpan step = TimeSpan.FromSeconds(20);
        for (TimeSpan elapsed = TimeSpan.Zero; elapsed < LeanProtocol.MaxAssemblyAge; elapsed += step)
        {
            Assert.That(node.Transport.AssemblyCount, Is.EqualTo(1), $"alive at {elapsed}");
            HashSet<ulong> cancelled = [.. a.Sent<CancelMessage>().Select(m => m.RequestId)];
            foreach (GetChunksMessage request in a.Sent<GetChunksMessage>().Where(r => !cancelled.Contains(r.RequestId)))
            {
                int next = delivered.GetValueOrDefault(request.RequestId);
                if (next == request.Indices.Length) continue;
                Deliver(node, peerA, Chunk(request.RequestId, descriptor, body, tree, request.Indices[next]));
                delivered[request.RequestId] = next + 1;
            }
            if (elapsed == TimeSpan.FromSeconds(140)) node.Transport.OnAnnounce(peerC, new AnnounceObjectsMessage([descriptor]));
            node.Tick(step);
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.Transport.AssemblyCount, Is.Zero);
            Assert.That(node.Transport.IncompleteBytes, Is.Zero);
            Assert.That(a.Penalties.Concat(c.Penalties), Is.Empty);
        }
    }

    [Test]
    public async Task Cancelled_fetch_discards_late_responses_without_penalty()
    {
        using LeanTestNode node = new();
        FakeLink a = new();
        LeanPeer peer = node.Connect(a);
        byte[] body = OpaqueWrapperBody(2 * ChunkBytes);
        LeanDescriptor descriptor = Describe(body, out LeanChunkTree tree);
        using CancellationTokenSource cancellation = new();
        Task<byte[]?> fetch = node.Transport.FetchBodyAsync(descriptor, null, peer, cancellation.Token);
        GetChunksMessage request = a.Sent<GetChunksMessage>().Single();

        await cancellation.CancelAsync();
        Assert.That(await fetch.WaitAsync(Timeout), Is.Null);
        Assert.That(peer.Live.Keys, Is.EqualTo(new[] { request.RequestId }), "a cancelled request keeps its slot until its terminal response");
        Serve(node, peer, request, descriptor, body, tree);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(peer.Live, Is.Empty, "Served crossing Cancel releases the slot");
            Assert.That(a.Sent<CancelMessage>().Select(m => m.RequestId), Is.EqualTo(new[] { request.RequestId }));
            Assert.That(a.Penalties, Is.Empty, "a response to an issued request that is no longer live is discarded");
            Assert.That(node.Transport.AssemblyCount, Is.Zero);
            Assert.That(node.Transport.IncompleteBytes, Is.Zero, "discarded before allocation");
        }
    }

    [TestCase("objects")]
    [TestCase("transactions")]
    public async Task Responses_after_cancellation_release_the_slot_without_using_their_data(string kind)
    {
        using LeanTestNode node = new();
        FakeLink a = new();
        LeanPeer peer = node.Connect(a);
        using CancellationTokenSource cancellation = new();
        Task response = kind == "objects"
            ? node.Transport.RequestObjectsAsync(peer, [new LeanSelector(LeanProtocol.KindWrapper, LeanObjectTransport.LocalProfile,
                LeanProtocol.LookupPrimary, TestItem.KeccakA.ValueHash256)], cancellation.Token)
            : node.Transport.RequestTransactionsAsync(peer, [TestItem.KeccakA.ValueHash256], cancellation.Token);
        await cancellation.CancelAsync();
        await response.WaitAsync(Timeout);
        Assert.That(peer.Live, Has.Count.EqualTo(1), "a cancelled request keeps its slot");

        // Unchecked after cancellation: a descriptor not matching its selector, or an envelope not matching its hash.
        if (kind == "objects")
            node.Transport.OnObjects(peer, new ObjectsMessage(1, [new LeanObjectResult(LeanResultStatus.Ok, Describe(OpaqueWrapperBody(10), out _), null)]));
        else
            node.Transport.OnTransactions(peer, new TransactionsMessage(1, [(LeanResultStatus.Ok, [1, 2, 3])]));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(a.Sent<CancelMessage>().Select(m => m.RequestId), Is.EqualTo(new[] { 1UL }));
            Assert.That(peer.Live, Is.Empty, "the terminal response releases the slot");
            Assert.That(a.Penalties, Is.Empty);
        }
    }

    [Test]
    public async Task Held_object_of_a_kind_the_peer_does_not_support_is_not_served()
    {
        using LeanTestNode node = new();
        FakeLink a = new();
        LeanPeer peer = node.Connect(a, LeanTestNode.Status(kinds: 1));
        byte[] package = InclusionList(10, FrameTransaction(14));
        Assert.That((await node.Wrappers.AcceptInclusionListDetailedAsync(package)).HasValidProof, Is.True);
        LeanDescriptor descriptor = LeanDescriptor.Create(LeanProtocol.KindInclusionList, LeanObjectTransport.LocalProfile,
            LeanDescriptor.InclusionListContext(ValueKeccak.Compute(package)), package, out _);
        await Until(() => node.Transport.IsStored(descriptor.ObjectId));

        node.Transport.OnGetChunks(peer, new GetChunksMessage(1, descriptor.ObjectId, [0]));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(a.Sent<AnnounceObjectsMessage>(), Is.Empty, "objects are announced only in kinds both peers support");
            Assert.That(a.Sent<CompleteMessage>().Single().Status, Is.EqualTo(LeanCompleteStatus.Unsupported));
            Assert.That(a.Chunks, Is.Empty);
        }
    }

    [Test]
    public async Task Envelopes_offered_by_hash_stay_recoverable_after_leaving_the_pool()
    {
        using LeanTestNode node = new();
        FakeLink a = new();
        LeanPeer peer = node.Connect(a);
        Transaction transaction = FrameTransaction(15);
        node.Pending.Add(transaction);
        byte[] wrapper = Wrapper(10, true, transaction);
        Assert.That((await node.Wrappers.AcceptDetailedAsync(wrapper)).HasValidProof, Is.True);
        await Until(() => node.Transport.IsStored(Describe(wrapper, out _).ObjectId));
        node.Pending.Clear();

        node.Transport.OnGetTransactions(peer, new GetTransactionsMessage(1, [transaction.Hash!.ValueHash256]));
        (LeanResultStatus status, byte[] envelope) = a.Sent<TransactionsMessage>().Single().Results.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(status, Is.EqualTo(LeanResultStatus.Ok));
            Assert.That(envelope, Is.EqualTo(Envelope(transaction)));
        }
    }

    [Test]
    public async Task Invalid_claimed_proof_is_rejected_before_any_transaction_is_fetched()
    {
        using LeanTestNode node = new();
        node.Verifier.Valid = false;
        FakeLink a = new();
        LeanPeer peer = node.Connect(a);
        byte[] body = Wrapper(ChunkBytes, true, FrameTransaction(16));
        LeanDescriptor descriptor = Describe(body, out LeanChunkTree tree);
        node.Transport.OnAnnounce(peer, new AnnounceObjectsMessage([descriptor]));
        ServeAll(node, peer, a, descriptor, body, tree, []);

        await Until(() => node.Transport.IsTombstoned(descriptor.ObjectId));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(a.Sent<GetTransactionsMessage>(), Is.Empty);
            Assert.That(a.Penalties, Is.Not.Empty);
        }
    }

    [Test]
    public async Task Recovered_transactions_must_match_the_claimed_dependencies()
    {
        using LeanTestNode node = new();
        FakeLink a = new();
        LeanPeer peer = node.Connect(a);
        Transaction transaction = FrameTransaction(17);
        List<FrameDependency> claimed = [Dependency(99)];
        byte[] body = MempoolWrapperDecoder.Instance.Encode(new MempoolWrapper
        {
            Transactions = [new WrapperTransaction(transaction.Hash!)],
            Deps = claimed,
            Mode = MempoolWrapper.ModeRecursive,
            RecursiveStark = new RecursiveStark(Proof(ChunkBytes), new Hash256(Eip8288Dependencies.ComputeDepsHash(claimed)))
        }).Bytes;
        LeanDescriptor descriptor = Describe(body, out LeanChunkTree tree);
        node.Transport.OnAnnounce(peer, new AnnounceObjectsMessage([descriptor]));
        ServeAll(node, peer, a, descriptor, body, tree, []);
        await Until(() => a.Sent<GetTransactionsMessage>().Length == 1);

        node.Transport.OnTransactions(peer, new TransactionsMessage(a.Sent<GetTransactionsMessage>()[0].RequestId,
            [(LeanResultStatus.Ok, Envelope(transaction))]));
        await Until(() => node.Transport.IsTombstoned(descriptor.ObjectId));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.Transport.IsStored(descriptor.ObjectId), Is.False);
            Assert.That(node.Pending, Is.Empty);
            Assert.That(a.Penalties, Is.Not.Empty, "the preliminary proof check does not validate the wrapper");
        }
    }

    [Test]
    public async Task Completed_body_awaiting_validation_expires_at_its_deadline()
    {
        using LeanTestNode node = new();
        using ManualResetEventSlim gate = new(false);
        node.Verifier.Gate = gate;
        FakeLink a = new();
        LeanPeer peer = node.Connect(a);
        byte[] body = Wrapper(ChunkBytes, false, FrameTransaction(18));
        LeanDescriptor descriptor = Describe(body, out LeanChunkTree tree);
        node.Transport.OnAnnounce(peer, new AnnounceObjectsMessage([descriptor]));
        ServeAll(node, peer, a, descriptor, body, tree, []);
        await node.Verifier.Entered.Task.WaitAsync(Timeout);

        node.Tick(LeanProtocol.MaxAssemblyIdle);
        gate.Set();
        await Until(() => node.Transport.CompletionBytes == 0 && node.Transport.AssemblyCount == 0);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.Transport.IsStored(descriptor.ObjectId), Is.False);
            Assert.That(node.Pending, Is.Empty, "expiry cancels admission of a body still being validated");
            Assert.That(a.Penalties, Is.Empty);
        }
    }

    [TestCase(0UL)]
    [TestCase(2UL)]
    public void Responses_to_unissued_request_ids_are_violations(ulong requestId)
    {
        using LeanTestNode node = new();
        FakeLink a = new();
        LeanPeer peer = node.Connect(a);
        node.Transport.OnAnnounce(peer, new AnnounceObjectsMessage([Describe(OpaqueWrapperBody(10), out _)]));
        node.Transport.OnComplete(peer, new CompleteMessage(requestId, default, LeanCompleteStatus.Unavailable));
        Assert.That(a.Penalties, Has.Length.EqualTo(1));
    }

    private static IEnumerable<TestCaseData> Misbehaviour()
    {
        yield return new TestCaseData("chunk length").SetName("Wrong final-chunk length");
        yield return new TestCaseData("branch depth").SetName("Wrong branch depth");
        yield return new TestCaseData("bad branch").SetName("Bad branch");
        yield return new TestCaseData("duplicate in request").SetName("Second chunk for one index in a request");
        yield return new TestCaseData("out of order").SetName("Chunk skips the next requested index");
        yield return new TestCaseData("wrong response type").SetName("Objects answers a GetChunks request");
        yield return new TestCaseData("other object").SetName("Chunk names another object");
        yield return new TestCaseData("served missing").SetName("Served with chunks missing");
        yield return new TestCaseData("complete other object").SetName("Complete names another object");
        yield return new TestCaseData("objects mismatch").SetName("Objects descriptor does not match its selector");
        yield return new TestCaseData("objects count").SetName("Objects result count differs");
        yield return new TestCaseData("transactions hash").SetName("Transaction envelope does not match its hash");
        yield return new TestCaseData("incoming id gap").SetName("Request ID gap");
        yield return new TestCaseData("index equal to N").SetName("GetChunks index equal to N");
    }

    [TestCaseSource(nameof(Misbehaviour))]
    public async Task Invalid_peer_data_stops_the_transfer_and_penalizes_the_peer(string scenario)
    {
        using LeanTestNode node = new();
        FakeLink a = new();
        LeanPeer peer = node.Connect(a);
        byte[] body = OpaqueWrapperBody(3 * ChunkBytes - 7);
        LeanDescriptor descriptor = Describe(body, out LeanChunkTree tree);
        LeanDescriptor other = Describe(OpaqueWrapperBody(10, 1), out _);
        node.Transport.OnAnnounce(peer, new AnnounceObjectsMessage([descriptor]));
        GetChunksMessage request = a.Sent<GetChunksMessage>().Single();
        int last = descriptor.ChunkCount - 1;
        switch (scenario)
        {
            case "chunk length":
                Deliver(node, peer, new ChunkMessage(1, descriptor.ObjectId, last, body.AsMemory(last * ChunkBytes, 10), tree.GetBranch(last)));
                break;
            case "branch depth":
                Deliver(node, peer, new ChunkMessage(1, descriptor.ObjectId, 0, body.AsMemory(0, ChunkBytes), []));
                break;
            case "bad branch":
                Deliver(node, peer, new ChunkMessage(1, descriptor.ObjectId, 0, body.AsMemory(0, ChunkBytes), tree.GetBranch(1)));
                break;
            case "duplicate in request":
                Deliver(node, peer, Chunk(1, descriptor, body, tree, 0));
                Deliver(node, peer, Chunk(1, descriptor, body, tree, 0));
                break;
            case "other object":
                Deliver(node, peer, new ChunkMessage(1, other.ObjectId, 0, body.AsMemory(0, ChunkBytes), tree.GetBranch(0)));
                break;
            case "out of order":
                Deliver(node, peer, Chunk(1, descriptor, body, tree, request.Indices[1]));
                break;
            case "wrong response type":
                node.Transport.OnObjects(peer, new ObjectsMessage(1, [LeanObjectResult.Of(LeanResultStatus.Busy)]));
                break;
            case "served missing":
                Serve(node, peer, request, descriptor, body, tree, count: 1);
                break;
            case "complete other object":
                node.Transport.OnComplete(peer, new CompleteMessage(1, other.ObjectId, LeanCompleteStatus.Busy));
                break;
            case "objects mismatch" or "objects count":
                {
                    LeanSelector selector = new(LeanProtocol.KindWrapper, LeanObjectTransport.LocalProfile, LeanProtocol.LookupPrimary, descriptor.ObjectId);
                    Task<LeanObjectResult[]?> objects = node.Transport.RequestObjectsAsync(peer, [selector], CancellationToken.None);
                    ulong id = a.Sent<GetObjectsMessage>().Single().RequestId;
                    node.Transport.OnObjects(peer, new ObjectsMessage(id, scenario == "objects count"
                        ? [LeanObjectResult.Of(LeanResultStatus.Busy), LeanObjectResult.Of(LeanResultStatus.Busy)]
                        : [new LeanObjectResult(LeanResultStatus.Ok, other, null)]));
                    Assert.That(await objects.WaitAsync(Timeout), Is.Null);
                    break;
                }
            case "transactions hash":
                {
                    Task<(LeanResultStatus, byte[])[]?> transactions = node.Transport.RequestTransactionsAsync(peer, [TestItem.KeccakA.ValueHash256], CancellationToken.None);
                    ulong id = a.Sent<GetTransactionsMessage>().Single().RequestId;
                    node.Transport.OnTransactions(peer, new TransactionsMessage(id, [(LeanResultStatus.Ok, [1, 2, 3])]));
                    Assert.That(await transactions.WaitAsync(Timeout), Is.Null);
                    break;
                }
            case "incoming id gap":
                node.Transport.OnGetChunks(peer, new GetChunksMessage(2, descriptor.ObjectId, [0]));
                break;
            case "index equal to N":
                node.Transport.PublishWrapper(Wrapper(10, false, FrameTransaction(9)));
                LeanDescriptor published = Describe(Wrapper(10, false, FrameTransaction(9)), out _);
                node.Transport.OnGetChunks(peer, new GetChunksMessage(1, published.ObjectId, [published.ChunkCount]));
                break;
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(a.Penalties, Has.Length.EqualTo(1));
            Assert.That(node.Transport.PeerCount, Is.Zero, "the transfer stops with the peer");
        }
    }

    [Test]
    public async Task Invalid_object_is_tombstoned_and_its_announcers_penalized()
    {
        using LeanTestNode node = new();
        FakeLink a = new("a"), b = new("b");
        LeanPeer peerA = node.Connect(a), peerB = node.Connect(b);
        byte[] body = OpaqueWrapperBody(2 * ChunkBytes);
        LeanDescriptor descriptor = Describe(body, out LeanChunkTree tree);
        node.Transport.OnAnnounce(peerA, new AnnounceObjectsMessage([descriptor]));
        ServeAll(node, peerA, a, descriptor, body, tree, []);

        await Until(() => node.Transport.IsTombstoned(descriptor.ObjectId));
        node.Transport.OnAnnounce(peerB, new AnnounceObjectsMessage([descriptor]));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(a.Penalties, Has.Length.EqualTo(1));
            Assert.That(b.Sent<GetChunksMessage>(), Is.Empty, "a tombstoned object is not fetched again");
            Assert.That(node.Transport.IsStored(descriptor.ObjectId), Is.False, "never served");
            Assert.That(node.Transport.CompletionBytes, Is.Zero);
        }
    }

    [Test]
    public async Task Content_hash_mismatch_with_authenticated_chunks_is_rejected_after_reconstruction()
    {
        using LeanTestNode node = new();
        FakeLink a = new();
        LeanPeer peer = node.Connect(a);
        byte[] body = Wrapper(2 * ChunkBytes, false, FrameTransaction(4));
        byte[] context = LeanDescriptor.WrapperContext();
        ValueHash256 bogus = ValueKeccak.Compute("bogus"u8);
        LeanChunkTree tree = new(LeanCommitment.Seed(LeanProtocol.KindWrapper, LeanObjectTransport.LocalProfile, LeanCommitment.ContextHash(context),
            bogus, (ulong)body.Length), body);
        LeanDescriptor descriptor = LeanDescriptor.Decode(LeanRlp.EncodeList(LeanRlp.EncodeUInt(LeanProtocol.KindWrapper),
            LeanRlp.EncodeBytes(LeanObjectTransport.LocalProfile.Bytes), context, LeanRlp.EncodeUInt((ulong)body.Length),
            LeanRlp.EncodeBytes(bogus.Bytes), LeanRlp.EncodeBytes(tree.ChunkRoot.Bytes)));
        node.Transport.OnAnnounce(peer, new AnnounceObjectsMessage([descriptor]));
        ServeAll(node, peer, a, descriptor, body, tree, []);

        await Until(() => node.Transport.IsTombstoned(descriptor.ObjectId));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(a.Penalties, Has.Length.EqualTo(1));
            Assert.That(node.Pending, Is.Empty, "nothing is admitted from an object that failed reconstruction");
        }
    }

    [Test]
    public async Task Pool_rejection_after_a_valid_proof_is_retained_without_penalty()
    {
        using LeanTestNode node = new() { Reject = true };
        FakeLink a = new();
        LeanPeer peer = node.Connect(a);
        byte[] body = Wrapper(ChunkBytes, false, FrameTransaction(5));
        LeanDescriptor descriptor = Describe(body, out LeanChunkTree tree);
        node.Transport.OnAnnounce(peer, new AnnounceObjectsMessage([descriptor]));
        ServeAll(node, peer, a, descriptor, body, tree, []);

        await Until(() => node.Transport.IsStored(descriptor.ObjectId));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(a.Penalties, Is.Empty);
            Assert.That(node.Pending, Is.Empty, "a retained proof object is not presented as admitted");
        }
    }

    [Test]
    public async Task Busy_verification_is_retried_and_completion_copies_stay_bounded()
    {
        using LeanTestNode node = new();
        FakeLink a = new();
        LeanPeer peer = node.Connect(a);
        using ManualResetEventSlim gate = new();
        node.Verifier.Gate = gate;
        // An RPC submission holds the single admission slot inside proof verification.
        Task<Consensus.ProofAggregation.ProofWrapperAcceptance> rpc = Task.Run(() => node.Wrappers.AcceptDetailedAsync(Wrapper(1, false, FrameTransaction(100))));
        await node.Verifier.Entered.Task.WaitAsync(Timeout);

        int count = LeanLimits.MaxPendingValidations + 3;
        LeanDescriptor[] descriptors = new LeanDescriptor[count];
        for (int i = 0; i < count; i++)
        {
            byte[] body = Wrapper(ChunkBytes, false, FrameTransaction(i));
            descriptors[i] = Describe(body, out LeanChunkTree tree);
            a.Clear();
            node.Transport.OnAnnounce(peer, new AnnounceObjectsMessage([descriptors[i]]));
            ServeAll(node, peer, a, descriptors[i], body, tree, []);
        }
        Assert.That(node.Transport.CompletionBytes, Is.LessThanOrEqualTo(LeanLimits.MaxCompletionBytes));
        Assert.That(node.Transport.IncompleteBytes, Is.Positive, "completed assemblies beyond the validation backlog stay charged");

        gate.Set();
        await rpc.WaitAsync(Timeout);
        for (int i = 0; i < 20 && !descriptors.All(d => node.Transport.IsStored(d.ObjectId)); i++)
        {
            node.Tick(TimeSpan.FromMilliseconds(250));
            await Task.Delay(100);
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(descriptors.Select(d => node.Transport.IsStored(d.ObjectId)), Is.All.True);
            Assert.That(a.Penalties, Is.Empty, "Busy verification is not misbehaviour");
            Assert.That(node.Transport.CompletionBytes, Is.Zero);
            Assert.That(node.Transport.IncompleteBytes, Is.Zero);
        }
    }

    [TestCase(LeanResultStatus.Ok)]
    [TestCase(LeanResultStatus.TooLarge)]
    public async Task Hash_entries_are_recovered_from_the_source_before_validation(LeanResultStatus transactionStatus)
    {
        using LeanTestNode node = new();
        FakeLink a = new();
        LeanPeer peer = node.Connect(a);
        Transaction transaction = FrameTransaction(6);
        byte[] body = Wrapper(ChunkBytes, true, transaction);
        LeanDescriptor descriptor = Describe(body, out LeanChunkTree tree);
        node.Transport.OnAnnounce(peer, new AnnounceObjectsMessage([descriptor]));
        ServeAll(node, peer, a, descriptor, body, tree, []);

        await Until(() => a.Sent<GetTransactionsMessage>().Length == 1);
        GetTransactionsMessage request = a.Sent<GetTransactionsMessage>()[0];
        Assert.That(request.Hashes, Is.EqualTo(new[] { transaction.Hash!.ValueHash256 }));
        Assert.That(node.Pending, Is.Empty, "an unresolved wrapper is not admitted");
        byte[]? holder = null;
        if (transactionStatus == LeanResultStatus.Ok)
            node.Transport.OnTransactions(peer, new TransactionsMessage(request.RequestId, [(LeanResultStatus.Ok, Envelope(transaction))]));
        else
        {
            node.Transport.OnTransactions(peer, new TransactionsMessage(request.RequestId, [(LeanResultStatus.TooLarge, [])]));
            await Until(() => a.Sent<GetObjectsMessage>().Length == 1);
            GetObjectsMessage lookup = a.Sent<GetObjectsMessage>()[0];
            Assert.That(lookup.Selectors.Single(), Is.EqualTo(new LeanSelector(LeanProtocol.KindWrapper, LeanObjectTransport.LocalProfile,
                LeanProtocol.LookupTransaction, transaction.Hash!.ValueHash256)));
            holder = Wrapper(10, false, transaction);
            LeanDescriptor holderDescriptor = Describe(holder, out LeanChunkTree holderTree);
            a.Clear();
            node.Transport.OnObjects(peer, new ObjectsMessage(lookup.RequestId, [new LeanObjectResult(LeanResultStatus.Ok, holderDescriptor, null)]));
            await Until(() => a.Sent<GetChunksMessage>().Length > 0);
            ServeAll(node, peer, a, holderDescriptor, holder, holderTree, []);
        }

        await Until(() => node.Transport.IsStored(descriptor.ObjectId));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.Pending.Select(t => t.Hash), Does.Contain(transaction.Hash));
            Assert.That(a.Penalties, Is.Empty);
            if (holder is not null)
                Assert.That(node.Transport.IsStored(Describe(holder, out _).ObjectId), Is.False, "a recovery source is neither validated nor relayed");
        }
    }

    [TestCase("changed field")]
    [TestCase("other body")]
    public async Task Sidecar_that_does_not_rebuild_the_block_header_is_not_used(string mismatch)
    {
        using LeanTestNode node = new(manualTime: false);
        FakeLink a = new();
        LeanPeer peer = node.Connect(a);
        BlockHeader header = LeanTransportTests.ProofHeader(Proof(2 * ChunkBytes));
        BlockHeader forged = header.Clone();
        forged.ExtraData = [1];
        if (mismatch == "other body") forged.TxRoot = TestItem.KeccakA;
        // The forged skeleton is self-consistent, but its header is not the one the block hash commits to.
        LeanHeaderSkeleton skeleton = LeanHeaderSkeleton.FromHeader(forged, new Serialization.Rlp.HeaderDecoder());
        byte[] body = LeanBodies.EncodeBlockProof(header.RecursiveStark!.StarkProof);
        LeanDescriptor descriptor = LeanDescriptor.Create(LeanProtocol.KindBlockProof, LeanObjectTransport.LocalProfile,
            LeanDescriptor.BlockProofContext(header.Hash!.ValueHash256, header.Number, forged.TxRoot!.ValueHash256,
                header.RecursiveStark.BlockDepsHash.ValueHash256, skeleton.Hash), body, out LeanChunkTree tree);
        BlockHeader withoutProof = header.Clone();
        withoutProof.RecursiveStark = null;

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(2));
        Task<RecursiveStark?> fetch = node.Transport.TryGetAsync(new Block(withoutProof, new BlockBody()), deadline.Token);
        await Until(() => a.Sent<GetObjectsMessage>().Length > 0);
        GetObjectsMessage lookup = a.Sent<GetObjectsMessage>()[0];
        node.Transport.OnObjects(peer, new ObjectsMessage(lookup.RequestId, [new LeanObjectResult(LeanResultStatus.Ok, descriptor, skeleton)]));
        if (mismatch == "changed field")
        {
            await Until(() => a.Sent<GetChunksMessage>().Length > 0);
            ServeAll(node, peer, a, descriptor, body, tree, []);
        }

        Assert.That(await fetch.WaitAsync(Timeout), Is.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(a.Penalties, Has.Length.EqualTo(mismatch == "changed field" ? 1 : 0),
                "a sidecar whose header misses the block hash is invalid; one for another body is merely unusable");
            Assert.That(a.Sent<GetChunksMessage>(), mismatch == "other body" ? Is.Empty : Is.Not.Empty, "the context is checked before chunks");
        }
    }

    [Test]
    public async Task Cancel_during_a_chunk_write_terminates_the_request_once()
    {
        using LeanTestNode node = new();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeLink a = new()
        {
            OnChunk = async (_, token) =>
            {
                started.TrySetResult();
                await release.Task.WaitAsync(token);
                return true;
            }
        };
        LeanPeer peer = node.Connect(a);
        byte[] body = Wrapper(3 * ChunkBytes, false, FrameTransaction(7));
        Assert.That(node.Transport.PublishWrapper(body), Is.True);
        LeanDescriptor descriptor = Describe(body, out _);

        node.Transport.OnGetChunks(peer, new GetChunksMessage(1, descriptor.ObjectId, [0, 1, 2]));
        await started.Task.WaitAsync(Timeout);
        node.Transport.OnCancel(peer, new CancelMessage(1));
        node.Transport.OnCancel(peer, new CancelMessage(1));
        release.TrySetResult();
        await Task.Delay(100);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(a.Sent<CompleteMessage>().Select(m => m.Status), Is.EqualTo(new[] { LeanCompleteStatus.Cancelled }));
            Assert.That(a.Chunks, Has.Length.LessThanOrEqualTo(1));
            Assert.That(a.Penalties, Is.Empty);
        }
    }

    [Test]
    public async Task Served_chunks_verify_and_requests_beyond_the_window_are_busy()
    {
        using LeanTestNode node = new();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeLink a = new() { OnChunk = async (_, token) => { await release.Task.WaitAsync(token); return true; } };
        LeanPeer peer = node.Connect(a);
        byte[] body = Wrapper(3 * ChunkBytes, false, FrameTransaction(8));
        node.Transport.PublishWrapper(body);
        LeanDescriptor descriptor = Describe(body, out _);
        for (ulong id = 1; id <= LeanProtocol.MaxRequestsPerPeer + 1; id++)
            node.Transport.OnGetChunks(peer, new GetChunksMessage(id, descriptor.ObjectId, [0, 1 + (int)(id % 2)]));
        Assert.That(a.Sent<CompleteMessage>().Single().Status, Is.EqualTo(LeanCompleteStatus.Busy));

        release.TrySetResult();
        await Until(() => a.Sent<CompleteMessage>().Length == LeanProtocol.MaxRequestsPerPeer + 1);
        LeanBranchVerifier verifier = new(descriptor);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(a.Sent<CompleteMessage>().Count(m => m.Status == LeanCompleteStatus.Served), Is.EqualTo(LeanProtocol.MaxRequestsPerPeer));
            Assert.That(a.Chunks.All(c => verifier.Verify(c.Index, c.Data.Span, c.Branch)), Is.True);
        }
    }

    [Test]
    public void Lookups_answer_with_statuses_and_never_penalize()
    {
        using LeanTestNode node = new();
        FakeLink a = new(), small = new("small");
        LeanPeer peer = node.Connect(a);
        LeanPeer constrained = node.Connect(small, LeanTestNode.Status(maxObjectBytes: 16));
        Transaction transaction = FrameTransaction(10);
        byte[] body = Wrapper(1000, false, transaction);
        node.Transport.PublishWrapper(body);
        LeanDescriptor descriptor = Describe(body, out _);
        ValueHash256 profile = LeanObjectTransport.LocalProfile;
        LeanSelector[] selectors =
        [
            new(LeanProtocol.KindWrapper, profile, LeanProtocol.LookupPrimary, descriptor.ObjectId),
            new(LeanProtocol.KindWrapper, profile, LeanProtocol.LookupPrimary, TestItem.KeccakA.ValueHash256),
            new(LeanProtocol.KindWrapper, profile, LeanProtocol.LookupTransaction, transaction.Hash!.ValueHash256),
            new(LeanProtocol.KindWrapper, TestItem.KeccakB.ValueHash256, LeanProtocol.LookupPrimary, descriptor.ObjectId)
        ];
        Array.Sort(selectors);
        node.Transport.OnGetObjects(peer, new GetObjectsMessage(1, selectors));
        node.Transport.OnGetObjects(constrained, new GetObjectsMessage(1, [selectors.First(s => s.LookupKey == descriptor.ObjectId && s.ProfileId == profile)]));
        node.Pending.Add(transaction);
        node.Transport.OnGetTransactions(peer, new GetTransactionsMessage(2, [.. new[] { transaction.Hash.ValueHash256, TestItem.KeccakC.ValueHash256 }
            .OrderBy(h => h.ToString())]));

        Dictionary<LeanSelector, LeanResultStatus> results = selectors.Zip(a.Sent<ObjectsMessage>().Single().Results)
            .ToDictionary(p => p.First, p => p.Second.Status);
        TransactionsMessage transactions = a.Sent<TransactionsMessage>().Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[selectors.First(s => s.LookupKey == descriptor.ObjectId && s.ProfileId == profile)], Is.EqualTo(LeanResultStatus.Ok));
            Assert.That(results[selectors.First(s => s.LookupKind == LeanProtocol.LookupTransaction)], Is.EqualTo(LeanResultStatus.Ok));
            Assert.That(results[selectors.First(s => s.LookupKey == TestItem.KeccakA.ValueHash256)], Is.EqualTo(LeanResultStatus.Unavailable));
            Assert.That(results[selectors.First(s => s.ProfileId != profile)], Is.EqualTo(LeanResultStatus.Unsupported));
            Assert.That(small.Sent<ObjectsMessage>().Single().Results.Single().Status, Is.EqualTo(LeanResultStatus.TooLarge));
            Assert.That(transactions.Results.Select(r => r.Status), Is.EquivalentTo(new[] { LeanResultStatus.Ok, LeanResultStatus.Unavailable }));
            Assert.That(transactions.Results.Single(r => r.Status == LeanResultStatus.Ok).Envelope, Is.EqualTo(Envelope(transaction)));
            Assert.That(a.Penalties.Concat(small.Penalties), Is.Empty);
        }
    }

    [Test]
    public void Assembly_limits_refuse_work_without_penalty()
    {
        using LeanTestNode node = new();
        FakeLink a = new("a"), b = new("b");
        LeanPeer peerA = node.Connect(a), peerB = node.Connect(b);
        LeanDescriptor[] descriptors = [.. Enumerable.Range(0, LeanLimits.MaxAssemblies + 4).Select(i => Describe(OpaqueWrapperBody(100, i), out _))
            .OrderBy(d => d.ObjectId.ToString())];
        node.Transport.OnAnnounce(peerA, new AnnounceObjectsMessage(descriptors[..(LeanLimits.MaxAssembliesPerPeer + 2)]));
        Assert.That(node.Transport.AssemblyCount, Is.EqualTo(LeanLimits.MaxAssembliesPerPeer));
        for (int i = 0; i < descriptors.Length; i++) node.Transport.OnAnnounce(peerB, new AnnounceObjectsMessage([descriptors[i]]));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.Transport.AssemblyCount, Is.EqualTo(2 * LeanLimits.MaxAssembliesPerPeer));
            Assert.That(a.Penalties.Concat(b.Penalties), Is.Empty);
        }
    }

    [TestCase("chain")]
    [TestCase("genesis")]
    [TestCase("profile")]
    [TestCase("fork")]
    public void Incompatible_status_or_inactive_fork_disables_the_transport(string mismatch)
    {
        using LeanTestNode node = mismatch == "fork" ? new(spec: Prague.Instance) : new();
        LeanStatusMessage status = mismatch switch
        {
            "chain" => new(2, LeanTestNode.Genesis.ValueHash256, [LeanObjectTransport.LocalProfile], 1, LeanLimits.MaxObjectBytes),
            "genesis" => new(1, TestItem.KeccakA.ValueHash256, [LeanObjectTransport.LocalProfile], 1, LeanLimits.MaxObjectBytes),
            "profile" => new(1, LeanTestNode.Genesis.ValueHash256, [TestItem.KeccakA.ValueHash256], 1, LeanLimits.MaxObjectBytes),
            _ => LeanTestNode.Status()
        };
        FakeLink link = new();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.Transport.Accept(link, status), Is.Null);
            Assert.That(node.Transport.PublishWrapper(Wrapper(10, false, FrameTransaction(11))), Is.EqualTo(mismatch != "fork"));
            Assert.That(link.Penalties, Is.Empty);
        }
    }
}
