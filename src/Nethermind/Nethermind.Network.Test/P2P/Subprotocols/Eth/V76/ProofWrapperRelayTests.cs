// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Network.P2P.Subprotocols.Eth.V76;
using Nethermind.Network.P2P.Subprotocols.Eth.V76.Messages;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V76;

/// <summary>EIP-8518 wrapper discovery, retrieval, validation, relay and mempool aggregation.</summary>
[Parallelizable(ParallelScope.All)]
public class ProofWrapperRelayTests
{
    [Test]
    public async Task Fetch_ValidWrapper_AdmitsItsTransactions_AndRelaysIt()
    {
        using Context context = new();
        ProofWrapper wrapper = context.Wrapper(Tx(0, TestItem.KeccakA));
        FakePeer sender = context.Peer(wrapper);
        FakePeer other = context.Peer();

        await context.Relay.OnAnnouncement(sender, [Announcement(wrapper)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(context.Relay.TryGetHeld(wrapper.Hash.ValueHash256, out _), Is.True);
            context.TxPool.Received(1).SubmitTx(Arg.Is<Transaction>(tx => FrameTxClaimVerifier.HasAggregatedWitness(tx)), TxHandlingOptions.None);
            Assert.That(other.Announced, Has.Count.EqualTo(1));
            Assert.That(sender.Announced, Is.Empty, "the wrapper came from it");
            Assert.That(context.ProofCache.Covers(wrapper.Deps), Is.True, "the builder can cover its transactions");
        }
    }

    [Test]
    public async Task Fetch_TransactionsAlreadyPending_HoldsButDoesNotRelay()
    {
        using Context context = new();
        context.TxPool.SubmitTx(default!, default).ReturnsForAnyArgs(AcceptTxResult.AlreadyKnown);
        ProofWrapper wrapper = context.Wrapper(Tx(0, TestItem.KeccakA));
        FakePeer other = context.Peer();

        await context.Relay.OnAnnouncement(context.Peer(wrapper), [Announcement(wrapper)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(context.Relay.TryGetHeld(wrapper.Hash.ValueHash256, out _), Is.True);
            Assert.That(other.Announced, Is.Empty);
        }
    }

    public enum Mismatch { Length, DepsHash, TxsHash }

    [TestCase(Mismatch.Length)]
    [TestCase(Mismatch.DepsHash)]
    [TestCase(Mismatch.TxsHash)]
    public async Task Fetch_FalseHint_DisconnectsBeforeTheProofIsVerified(Mismatch mismatch)
    {
        using Context context = new();
        ProofWrapper wrapper = context.Wrapper(Tx(0, TestItem.KeccakA));
        FakePeer sender = context.Peer(wrapper);
        WrapperAnnouncement announcement = Announcement(wrapper);
        announcement = mismatch switch
        {
            Mismatch.Length => announcement with { Length = announcement.Length + 1 },
            Mismatch.DepsHash => announcement with { DepsHash = TestItem.KeccakF.ValueHash256 },
            _ => announcement with { TxsHash = TestItem.KeccakF.ValueHash256 },
        };

        await context.Relay.OnAnnouncement(sender, [announcement]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sender.Disconnected, Is.True);
            Assert.That(context.Verifier.Verifications, Is.Zero);
            Assert.That(context.Relay.TryGetHeld(wrapper.Hash.ValueHash256, out _), Is.False);
        }
    }

    [Test]
    public async Task Fetch_InvalidProof_DiscardsWithoutPenalty_AndIsNotFetchedAgain()
    {
        using Context context = new();
        ProofWrapper wrapper = ProofWrapperCodec.Create([ProofWrapperCodec.Normalize(Tx(0, TestItem.KeccakA))!], [Tx(0, TestItem.KeccakA)],
            Eip8288Dependencies.Canonicalize(ProofWrapperCodec.AggregatedDependencies(Tx(0, TestItem.KeccakA))), [1, 2, 3]);
        FakePeer sender = context.Peer(wrapper);

        await context.Relay.OnAnnouncement(sender, [Announcement(wrapper)]);
        await context.Relay.OnAnnouncement(context.Peer(wrapper), [Announcement(wrapper)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sender.Disconnected, Is.False);
            Assert.That(sender.Requests, Is.EqualTo(1));
            Assert.That(context.Relay.TryGetHeld(wrapper.Hash.ValueHash256, out _), Is.False);
            context.TxPool.DidNotReceiveWithAnyArgs().SubmitTx(default!, default);
        }
    }

    /// <summary>A peer serving an invalid wrapper under an honest hash cannot get that hash refused: it is the peer's fault,
    /// and the wrapper is fetched from another announcer.</summary>
    [Test]
    public async Task Fetch_InvalidWrapperUnderAnotherHash_BlamesThePeer_AndKeepsTheHashFetchable()
    {
        using Context context = new();
        ProofWrapper honest = context.Wrapper(Tx(0, TestItem.KeccakA));
        Transaction other = Tx(1, TestItem.KeccakB);
        ProofWrapper invalid = ProofWrapperCodec.Create([ProofWrapperCodec.Normalize(other)!], [other],
            [new AggregatedDependency(TestItem.KeccakE.ValueHash256, TestItem.KeccakF.ValueHash256)], [1]);
        FakePeer liar = context.Peer(honest);
        liar.Substitute = invalid.Encoded;
        FakePeer holder = context.Peer(honest);

        await context.Relay.OnAnnouncement(liar, [Announcement(honest)]);
        await context.Relay.OnAnnouncement(holder, [Announcement(honest)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(liar.Disconnected, Is.True);
            Assert.That(() => context.Relay.TryGetHeld(honest.Hash.ValueHash256, out _), Is.True.After(5000, 10));
        }
    }

    [Test]
    public async Task Fetch_NoTransactionPending_DoesNotHoldTheWrapper()
    {
        using Context context = new();
        context.TxPool.SubmitTx(default!, default).ReturnsForAnyArgs(AcceptTxResult.FeeTooLow);
        ProofWrapper wrapper = context.Wrapper(Tx(0, TestItem.KeccakA));

        await context.Relay.OnAnnouncement(context.Peer(wrapper), [Announcement(wrapper)]);

        Assert.That(context.Relay.HeldCount, Is.Zero);
    }

    /// <summary>EIP-8518 forbids penalizing a peer for proof failures but allows deprioritizing its fetches: after repeated
    /// failures its announcements are no longer fetched, and it stays connected.</summary>
    [Test]
    public async Task Fetch_RepeatedProofFailures_StopFetchingFromThePeer()
    {
        using Context context = new();
        FakePeer peer = context.Peer();
        for (int i = 0; i <= 8; i++)
        {
            Transaction tx = Tx((ulong)i, TestItem.KeccakA);
            ProofWrapper wrapper = ProofWrapperCodec.Create([ProofWrapperCodec.Normalize(tx)!], [tx],
                Eip8288Dependencies.Canonicalize(ProofWrapperCodec.AggregatedDependencies(tx)), [1, 2, 3]);
            peer.Served = wrapper;
            await context.Relay.OnAnnouncement(peer, [Announcement(wrapper)]);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(peer.Requests, Is.EqualTo(8));
            Assert.That(peer.Disconnected, Is.False);
        }
    }

    [Test]
    public async Task Fetch_ConcurrentFetchesFromOnePeer_AreCapped()
    {
        using Context context = new();
        FakePeer peer = context.Peer();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.Gate = release.Task;
        List<Task> fetches = [];
        for (int i = 0; i < 6; i++)
        {
            ProofWrapper wrapper = context.Wrapper(Tx((ulong)i, TestItem.KeccakA));
            fetches.Add(context.Relay.OnAnnouncement(peer, [Announcement(wrapper)]));
        }

        Assert.That(() => peer.Requests, Is.EqualTo(4).After(5000, 10));
        release.SetResult();
        await Task.WhenAll(fetches);
        Assert.That(peer.Requests, Is.EqualTo(4));
    }

    [Test]
    public async Task Fetch_RetainedWitnessFails_DiscardsTheWrapper()
    {
        using Context context = new();
        context.TxPool.SubmitTx(default!, default).ReturnsForAnyArgs(AcceptTxResult.Invalid.WithMessage(FrameTxClaimVerifier.InvalidClaim));
        ProofWrapper wrapper = context.Wrapper(Tx(0, TestItem.KeccakA, withSecp256k1Claim: true));
        FakePeer other = context.Peer();

        await context.Relay.OnAnnouncement(context.Peer(wrapper), [Announcement(wrapper)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(context.Relay.TryGetHeld(wrapper.Hash.ValueHash256, out _), Is.False);
            Assert.That(other.Announced, Is.Empty);
        }
    }

    [Test]
    public async Task Fetch_CompactCacheMiss_RetriesInFull()
    {
        using Context context = new();
        ProofWrapper wrapper = context.Wrapper(Tx(0, TestItem.KeccakA));
        FakePeer sender = context.Peer(wrapper);
        sender.CompactReferencesEverything = true;

        await context.Relay.OnAnnouncement(sender, [Announcement(wrapper)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sender.CompactFlags, Is.EqualTo(new[] { true, false }));
            Assert.That(sender.Disconnected, Is.False);
            Assert.That(context.Relay.TryGetHeld(wrapper.Hash.ValueHash256, out _), Is.True);
        }
    }

    [Test]
    public async Task Fetch_EmptyResponse_TriesAnotherAnnouncer()
    {
        using Context context = new();
        ProofWrapper wrapper = context.Wrapper(Tx(0, TestItem.KeccakA));
        FakePeer empty = context.Peer();
        FakePeer holder = context.Peer(wrapper);
        TaskCompletionSource firstAnswered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        empty.Gate = firstAnswered.Task;

        Task first = context.Relay.OnAnnouncement(empty, [Announcement(wrapper)]);
        await context.Relay.OnAnnouncement(holder, [Announcement(wrapper)]);
        firstAnswered.SetResult();
        await first;

        Assert.That(() => context.Relay.TryGetHeld(wrapper.Hash.ValueHash256, out _), Is.True.After(5000, 10));
        Assert.That(holder.Requests, Is.EqualTo(1));
    }

    [Test]
    public async Task Announcement_EquivalentCoverage_SkipsTheFetch()
    {
        using Context context = new();
        Transaction tx = Tx(0, TestItem.KeccakA);
        ProofWrapper held = context.Wrapper(tx);
        context.Relay.AddLocal(held);
        FakePeer peer = context.Peer();

        // Different proof bytes, equal coverage.
        await context.Relay.OnAnnouncement(peer, [Announcement(held) with { Hash = TestItem.KeccakF.ValueHash256 }]);

        Assert.That(peer.Requests, Is.Zero);
    }

    [Test]
    public async Task Inactive_IgnoresAnnouncements()
    {
        using Context context = new(Eip8141Prototype.Instance);
        ProofWrapper wrapper = context.Wrapper(Tx(0, TestItem.KeccakA));
        FakePeer sender = context.Peer(wrapper);

        await context.Relay.OnAnnouncement(sender, [Announcement(wrapper)]);

        Assert.That(sender.Requests, Is.Zero);
    }

    [Test]
    public void Serve_OnlyAnnouncedWrappers_AndReferencesEncodingsThePeerHolds()
    {
        using Context context = new();
        ProofWrapper wrapper = context.Wrapper(Tx(0, TestItem.KeccakA));
        FakePeer peer = context.Peer();
        FakePeer late = context.Peer();
        context.Relay.AddLocal(wrapper);
        context.Relay.RemovePeer(late);

        IReadOnlyList<ServedWrapper> full = context.Relay.ServeWrappers(peer, [wrapper.Hash.ValueHash256], compact: false);
        IReadOnlyList<ServedWrapper> compact = context.Relay.ServeWrappers(peer, [wrapper.Hash.ValueHash256], compact: true);
        IReadOnlyList<ServedManifest> manifests = context.Relay.ServeManifests(peer, [wrapper.Hash.ValueHash256, TestItem.KeccakF.ValueHash256]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(peer.Announced, Has.Count.EqualTo(1));
            Assert.That(full[0].Item, Is.EqualTo(wrapper.Encoded));
            Assert.That(compact[0].Item, Has.Length.LessThan(wrapper.Length), "the peer holds the encodings of a wrapper served to it");
            Assert.That(manifests, Has.Count.EqualTo(1));
            Assert.That(context.Relay.ServeWrappers(late, [wrapper.Hash.ValueHash256], compact: false), Is.Empty);
        }
    }

    [Test]
    public void Aggregate_PendingRawTransactions_ProducesAVerifyingWrapper()
    {
        using Context context = new();
        Transaction first = Tx(0, TestItem.KeccakA, withSecp256k1Claim: true);
        Transaction second = Tx(1, TestItem.KeccakB);
        context.TxPool.GetPendingTransactions().Returns([first, second]);

        ProofWrapper wrapper = context.Relay.Aggregate()!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(wrapper.Transactions, Has.Length.EqualTo(2));
            Assert.That(ProofWrapperCodec.VerifyProof(wrapper, context.ProofCache), Is.True);
            Assert.That(ProofWrapperCodec.TryDecode(wrapper.Encoded, null, wrapper.Length, out ProofWrapper? decoded, out _),
                Is.EqualTo(ProofWrapperCodec.Verdict.Valid));
            Assert.That(decoded!.Hash, Is.EqualTo(wrapper.Hash));
        }
    }

    /// <summary>A reorg returns a transaction with aggregated witnesses to the pool; the proof of the block it came from
    /// covers it, so it is re-aggregated into a wrapper peers can fetch.</summary>
    [Test]
    public void Aggregate_ReorgedAggregatedTransaction_IsReaggregatedFromItsBlocksProof()
    {
        using Context context = new();
        Transaction reorged = Tx(0, TestItem.KeccakA, withSecp256k1Claim: true);
        Transaction sibling = Tx(1, TestItem.KeccakB);
        Block block = Build.A.Block.WithTransactions(reorged, sibling).TestObject;
        List<AggregatedDependency> blockDeps = AggregatedDependencyProof.Aggregated(FrameTxDependencyProof.BlockDependencies(block));
        context.Relay.RememberBlockProof(block, context.Verifier.Prove(Eip8288Dependencies.ComputeDepsHash(blockDeps)));
        reorged.ClaimWitnesses![0] = FrameTxClaimVerifier.AggregatedWitness;
        context.TxPool.GetPendingTransactions().Returns([reorged]);

        ProofWrapper wrapper = context.Relay.Aggregate()!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(wrapper.Manifest, Is.EqualTo(new[] { reorged.Hash }));
            Assert.That(ProofWrapperCodec.VerifyProof(wrapper, context.ProofCache), Is.True);
            Assert.That(context.Verifier.LastInput!.RecursiveProofs, Has.Count.EqualTo(1), "extends the block's proof");
            Assert.That(context.Verifier.LastInput.Discards, Has.Count.EqualTo(1), "and discards the sibling's dependency");
        }
    }

    [Test]
    public void Aggregate_AggregatedTransactionNoProofCovers_IsLeftOut()
    {
        using Context context = new();
        Transaction orphan = Tx(0, TestItem.KeccakA);
        orphan.ClaimWitnesses![0] = FrameTxClaimVerifier.AggregatedWitness;
        context.TxPool.GetPendingTransactions().Returns([orphan]);

        Assert.That(context.Relay.Aggregate(), Is.Null);
    }

    private static Transaction Tx(ulong nonce, Hash256 message, bool withSecp256k1Claim = false) =>
        FrameTxTestFrames.LeanSphincsTx(nonce, withSecp256k1Claim, (TestItem.KeccakA, message));

    private static WrapperAnnouncement Announcement(ProofWrapper wrapper) =>
        new(wrapper.Length, wrapper.Hash.ValueHash256, wrapper.DepsHash, wrapper.TxsHash);

    private sealed class Context : IDisposable
    {
        public Context(IReleaseSpec? spec = null)
        {
            TxPool.SubmitTx(default!, default).ReturnsForAnyArgs(AcceptTxResult.Accepted);
            TxPool.GetPendingTransactions().Returns([]);
            ProofCache = new DependencyProofCache(Verifier, LimboLogs.Instance);
            Relay = new ProofWrapperRelay(TxPool, ProofCache, new TestSingleReleaseSpecProvider(spec ?? Eip8288Prototype.Instance),
                Substitute.For<IBlockTree>(), Timestamper.Default, new BlocksConfig { DependencyProofAggregation = DependencyProofAggregation.Mempool },
                LimboLogs.Instance);
        }

        public ITxPool TxPool { get; } = Substitute.For<ITxPool>();
        public FakeVerifier Verifier { get; } = new();
        public DependencyProofCache ProofCache { get; }
        public ProofWrapperRelay Relay { get; }

        public FakePeer Peer(ProofWrapper? served = null)
        {
            FakePeer peer = new(served);
            Relay.AddPeer(peer);
            return peer;
        }

        public ProofWrapper Wrapper(params Transaction[] transactions)
        {
            Array.Sort(transactions, static (left, right) => left.Hash!.CompareTo(right.Hash!));
            List<AggregatedDependency> deps = [];
            byte[][] encodings = new byte[transactions.Length][];
            for (int i = 0; i < transactions.Length; i++)
            {
                deps.AddRange(ProofWrapperCodec.AggregatedDependencies(transactions[i]));
                encodings[i] = ProofWrapperCodec.Normalize(transactions[i])!;
            }

            List<AggregatedDependency> canonical = Eip8288Dependencies.Canonicalize(deps);
            return ProofWrapperCodec.Create(encodings, transactions, canonical, Verifier.Prove(Eip8288Dependencies.ComputeDepsHash(canonical)));
        }

        public void Dispose() => Relay.Dispose();
    }

    /// <summary>A Lean backend whose proof of a statement is its dependency digest.</summary>
    private sealed class FakeVerifier : ILeanProofVerifier
    {
        private int _verifications;

        public int Verifications => Volatile.Read(ref _verifications);
        public AggregationInput? LastInput { get; private set; }

        public byte[] Prove(in ValueHash256 depsHash) => depsHash.ToByteArray();

        public void EnsureAvailable() { }

        public bool VerifyLeanSphincs(in ValueHash256 message, in ValueHash256 publicKey, ReadOnlySpan<byte> witness) => true;

        public bool VerifyDependencyProof(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, ReadOnlySpan<byte> proof)
        {
            Interlocked.Increment(ref _verifications);
            return proof.SequenceEqual(depsHash.Bytes);
        }

        public byte[] ProveDependencyProof(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, AggregationInput input)
        {
            LastInput = input;
            return Prove(depsHash);
        }
    }

    /// <summary>An <c>eth/76</c> peer serving one wrapper.</summary>
    private sealed class FakePeer(ProofWrapper? served) : IProofWrapperPeer
    {
        private int _requests;

        public ProofWrapper? Served { get; set; } = served;

        /// <summary>An item served in place of <see cref="Served"/>'s, under its hash.</summary>
        public byte[]? Substitute { get; set; }

        public List<WrapperAnnouncement> Announced { get; } = [];
        public List<bool> CompactFlags { get; } = [];
        public bool CompactReferencesEverything { get; set; }
        public bool Disconnected { get; private set; }
        public Task Gate { get; set; } = Task.CompletedTask;
        public int Requests => Volatile.Read(ref _requests);

        public void Announce(IReadOnlyList<WrapperAnnouncement> announcements) => Announced.AddRange(announcements);

        public async Task<IReadOnlyList<ServedWrapper>> GetProofWrappers(IReadOnlyList<ValueHash256> hashes, bool compact, CancellationToken token)
        {
            Interlocked.Increment(ref _requests);
            CompactFlags.Add(compact);
            await Gate;
            if (Served is not { } wrapper || !System.Linq.Enumerable.Contains(hashes, wrapper.Hash.ValueHash256)) return [];
            byte[] item = Substitute ?? (compact && CompactReferencesEverything
                ? ProofWrapperCodec.Encode(wrapper.EncodedTransactions, wrapper.Deps, wrapper.Proof, static _ => true)
                : wrapper.Encoded);
            return [new ServedWrapper(wrapper.Hash.ValueHash256, item)];
        }

        public Task<IReadOnlyList<ServedManifest>> GetProofWrapperManifests(IReadOnlyList<ValueHash256> hashes, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<ServedManifest>>([]);

        public void Disconnect(string reason) => Disconnected = true;
    }
}
