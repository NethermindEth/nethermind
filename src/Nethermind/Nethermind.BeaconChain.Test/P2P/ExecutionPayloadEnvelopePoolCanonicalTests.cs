// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.ReqResp;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.Types.SignedBeaconBlockBuilders;

namespace Nethermind.BeaconChain.Test.P2P;

// gloas/p2p-interface.md ExecutionPayloadEnvelopesByRange v1: envelopes from the current fork choice view only, and none for a
// block whose payload the chain treats as empty.
public class ExecutionPayloadEnvelopePoolCanonicalTests
{
    private static readonly ulong Base = FirstGloasSlot + 10;

    [TestCase(3UL, true, TestName = "block on the head chain is served, the other block at its slot is not")]
    [TestCase(1UL, false, TestName = "block past the requested count is not served")]
    public void Serves_only_the_head_chain_when_two_blocks_share_a_slot(ulong count, bool expectSecond)
    {
        EnvelopeChain chain = new();
        (Hash256 genesis, Hash256 genesisHash) = chain.Put(Base, Hash256.Zero, Hash256.Zero);
        (Hash256 onChain, Hash256 onChainHash) = chain.Put(Base + 1, genesis, genesisHash);
        (Hash256 fork, _) = chain.Put(Base + 1, genesis, genesisHash, salt: 1);
        (Hash256 head, _) = chain.Put(Base + 2, onChain, onChainHash);
        chain.AddEnvelopes(genesis, onChain, fork);
        chain.SetHead(head, Base + 2);

        Assert.That(chain.ServedRoots(Base, count), Is.EqualTo(expectSecond ? new[] { genesis, onChain } : new[] { genesis }));
    }

    [Test]
    public void Omits_a_block_whose_child_builds_on_the_payload_before_it()
    {
        EnvelopeChain chain = new();
        (Hash256 genesis, Hash256 genesisHash) = chain.Put(Base, Hash256.Zero, Hash256.Zero);
        (Hash256 empty, _) = chain.Put(Base + 1, genesis, genesisHash);
        (Hash256 head, _) = chain.Put(Base + 2, empty, genesisHash);
        chain.AddEnvelopes(genesis, empty);
        chain.SetHead(head, Base + 2);

        Assert.That(chain.ServedRoots(Base, 3), Is.EqualTo(new[] { genesis }), "the head's bid names the grandparent's payload, so its parent's is EMPTY");
    }

    [Test]
    public void Serves_the_head_only_when_its_verified_envelope_is_held([Values] bool held)
    {
        EnvelopeChain chain = new();
        (Hash256 genesis, Hash256 genesisHash) = chain.Put(Base, Hash256.Zero, Hash256.Zero);
        (Hash256 head, _) = chain.Put(Base + 1, genesis, genesisHash);
        chain.AddEnvelopes(genesis);
        if (held)
        {
            chain.AddEnvelopes(head);
        }

        chain.SetHead(head, Base + 1);

        Assert.That(chain.ServedRoots(Base, 2), Is.EqualTo(held ? new[] { genesis, head } : new[] { genesis }));
    }

    [Test]
    public void Ignores_a_stale_canonical_index_entry_at_a_slot_the_head_chain_skips()
    {
        EnvelopeChain chain = new();
        (Hash256 genesis, Hash256 genesisHash) = chain.Put(Base, Hash256.Zero, Hash256.Zero);
        (Hash256 orphan, _) = chain.Put(Base + 1, genesis, genesisHash);
        (Hash256 head, _) = chain.Put(Base + 2, genesis, genesisHash);
        chain.Store.SetCanonicalRoot(Base, genesis);
        chain.Store.SetCanonicalRoot(Base + 1, orphan);
        chain.Store.SetCanonicalRoot(Base + 2, head);
        chain.AddEnvelopes(genesis, orphan, head);
        chain.SetHead(head, Base + 2);

        Assert.That(chain.ServedRoots(Base, 3), Is.EqualTo(new[] { genesis, head }), "a reorged-out root the index still names is not served");
    }

    [Test]
    public void Serves_nothing_without_a_store_or_a_status([Values] bool withStore)
    {
        EnvelopeChain chain = new(withStore, withStatus: !withStore);
        (Hash256 head, _) = chain.Put(Base, Hash256.Zero, Hash256.Zero);
        chain.AddEnvelopes(head);
        chain.SetHead(head, Base);

        Assert.That(chain.ServedRoots(Base, 1), Is.Empty);
    }

    [Test]
    public void Skips_pre_Gloas_blocks([Values] bool preGloasHead)
    {
        EnvelopeChain chain = new();
        Hash256 fulu = chain.PutFulu(FirstGloasSlot - 1);
        (Hash256 first, Hash256 firstHash) = chain.Put(FirstGloasSlot, fulu, Hash256.Zero);
        (Hash256 head, _) = chain.Put(FirstGloasSlot + 1, first, firstHash);
        chain.AddEnvelopes(fulu, first, head);
        chain.SetHead(preGloasHead ? fulu : head, preGloasHead ? FirstGloasSlot - 1 : FirstGloasSlot + 1);

        Assert.That(chain.ServedRoots(FirstGloasSlot - 1, 3), Is.EqualTo(preGloasHead ? [] : new[] { first, head }));
        long before = chain.BlockReads;
        chain.ServedRoots(FirstGloasSlot - 1, 3);
        Assert.That(chain.BlockReads - before, Is.Zero, "a pre-Gloas block that ends the walk is decoded once, not once per request");
    }

    [Test]
    public void Range_further_below_the_head_than_the_walk_cap_is_resource_unavailable()
    {
        EnvelopeChain chain = new();
        Hash256 root = Hash256.Zero;
        Hash256 blockHash = Hash256.Zero;
        for (ulong slot = Base; slot <= Base + ExecutionPayloadEnvelopePool.MaxCanonicalWalk; slot++)
        {
            (root, blockHash) = chain.Put(slot, root, blockHash);
        }

        chain.SetHead(root, Base + ExecutionPayloadEnvelopePool.MaxCanonicalWalk);

        Assert.That(() => chain.Pool.GetCanonical(Base + 1, 1), Throws.Nothing, "reaching the start takes exactly the cap");
        Eth2ReqRespException? thrown = Assert.Throws<Eth2ReqRespException>(() => chain.Pool.GetCanonical(Base, 1));
        Assert.That(thrown!.ResponseCode, Is.EqualTo(ReqRespFraming.ResponseCode.ResourceUnavailable), "one block past the cap");
    }

    [Test]
    public void Repeated_range_past_the_walk_cap_reads_the_cap_once_and_nothing_after()
    {
        EnvelopeChain chain = new();
        Hash256 head = Hash256.Zero;
        Hash256 headHash = Hash256.Zero;
        ulong headSlot = Base + ExecutionPayloadEnvelopePool.MaxCanonicalWalk;
        for (ulong slot = Base; slot <= headSlot; slot++)
        {
            (head, headHash) = chain.Put(slot, head, headHash);
        }

        (Hash256 fork, _) = chain.Put(headSlot + 1, head, headHash);
        chain.SetHead(head, headSlot);

        long before = chain.BlockReads;
        Assert.Throws<Eth2ReqRespException>(() => chain.Pool.GetCanonical(Base, 1));
        long first = chain.BlockReads - before;
        chain.SetHead(fork, headSlot + 1);
        chain.ServedRoots(headSlot + 1, 1);
        chain.SetHead(head, headSlot);
        long beforeRepeat = chain.BlockReads;
        Assert.Throws<Eth2ReqRespException>(() => chain.Pool.GetCanonical(Base, 1));

        Assert.That((first, chain.BlockReads - beforeRepeat), Is.EqualTo(((long)ExecutionPayloadEnvelopePool.MaxCanonicalWalk, 0L)),
            "the cap is checked before a read, and a full walk plus another root stays memoized");
    }

    [Test]
    public void Repeated_request_reads_no_block_from_the_store()
    {
        EnvelopeChain chain = new();
        (Hash256 genesis, Hash256 genesisHash) = chain.Put(Base, Hash256.Zero, Hash256.Zero);
        (Hash256 head, _) = chain.Put(Base + 1, genesis, genesisHash);
        chain.AddEnvelopes(genesis, head);
        chain.SetHead(head, Base + 1);

        long before = chain.BlockReads;
        chain.ServedRoots(Base, 2);
        long first = chain.BlockReads - before;
        chain.ServedRoots(Base, 2);

        Assert.That((first, chain.BlockReads - before - first), Is.EqualTo((2L, 0L)), "each block is decoded once, not once per request");
    }
}

/// <summary>A Sepolia store of Gloas blocks with bid hashes chosen per block, the head status, and a pool reading both.</summary>
internal sealed class EnvelopeChain
{
    public static readonly BeaconChainSpec Spec = Sepolia;

    private readonly MemColumnsDb<BeaconChainDbColumns> _db = new();
    private readonly Dictionary<Hash256, (ulong Slot, Hash256 BlockHash)> _blocks = [];

    public EnvelopeChain(bool withStore = true, bool withStatus = true)
    {
        Store = new BeaconChainStore(_db, Spec);
        Status = new BeaconChainStatusHolder(Spec, Timestamper.Default)
        {
            CurrentStatus = new StatusMessageV2
            {
                ForkDigest = ForkDigest.Compute(Spec, Spec.GloasForkEpoch),
                FinalizedRoot = Hash256.Zero,
                FinalizedEpoch = Spec.GloasForkEpoch,
                HeadRoot = Hash256.Zero,
                HeadSlot = FirstGloasSlot,
                EarliestAvailableSlot = FirstGloasSlot,
            },
        };
        Pool = new ExecutionPayloadEnvelopePool(store: withStore ? Store : null, status: withStatus ? Status : null);
    }

    public BeaconChainStore Store { get; }

    public BeaconChainStatusHolder Status { get; }

    public ExecutionPayloadEnvelopePool Pool { get; }

    public long BlockReads => ((MemDb)_db.GetColumnDb(BeaconChainDbColumns.Blocks)).ReadsCount;

    /// <summary>Stores a Gloas block whose bid names <paramref name="parentBlockHash"/> and a block hash of its own.</summary>
    /// <param name="salt">Tells apart two blocks at one slot with one parent.</param>
    public (Hash256 Root, Hash256 BlockHash) Put(ulong slot, Hash256 parentRoot, Hash256 parentBlockHash, int salt = 0)
    {
        SignedBeaconBlockGloas block = CreateMinimalGloasBlock(slot, parentRoot);
        ExecutionPayloadBid bid = block.Message!.Body!.SignedExecutionPayloadBid!.Message!;
        bid.ParentBlockHash = parentBlockHash;
        bid.BlockHash = Keccak.Compute($"payload {slot} {salt}");
        bid.ExecutionRequestsRoot = SszRoots.HashTreeRoot(new ExecutionRequestsGloas());
        Hash256 root = SszRoots.HashTreeRoot(block.Message);
        Store.PutForkedBlock(root, new ForkedSignedBeaconBlock.OfGloas(block));
        _blocks[root] = (slot, bid.BlockHash);
        return (root, bid.BlockHash);
    }

    public Hash256 PutFulu(ulong slot)
    {
        SignedBeaconBlock block = CreateMinimalBlock(slot);
        Hash256 root = SszRoots.HashTreeRoot(block.Message!);
        Store.PutBlock(root, block);
        _blocks[root] = (slot, Hash256.Zero);
        return root;
    }

    /// <summary>Overwrites the stored block <paramref name="root"/> with bytes that are not snappy.</summary>
    public void Corrupt(Hash256 root) => _db.GetColumnDb(BeaconChainDbColumns.Blocks)[root.Bytes] = [0xFF];

    public void AddEnvelopes(params Hash256[] roots)
    {
        foreach (Hash256 root in roots)
        {
            Pool.Add(root, Envelope(root));
        }
    }

    public void SetHead(Hash256 root, ulong slot) =>
        Status.CurrentStatus = new StatusMessageV2
        {
            ForkDigest = Status.CurrentStatus.ForkDigest,
            FinalizedRoot = Hash256.Zero,
            FinalizedEpoch = Status.CurrentStatus.FinalizedEpoch,
            HeadRoot = root,
            HeadSlot = slot,
            EarliestAvailableSlot = Status.CurrentStatus.EarliestAvailableSlot,
        };

    public Hash256[] ServedRoots(ulong startSlot, ulong count) => [.. Pool.GetCanonical(startSlot, count).Select(static e => e.Message!.BeaconBlockRoot!)];

    /// <summary>An envelope matching the stored block <paramref name="root"/> and its bid, bar the signature.</summary>
    public SignedExecutionPayloadEnvelope Envelope(Hash256 root) => new()
    {
        Message = new ExecutionPayloadEnvelope
        {
            Payload = new ExecutionPayloadGloas { SlotNumber = _blocks[root].Slot, BlockHash = _blocks[root].BlockHash, Withdrawals = [] },
            ExecutionRequests = new ExecutionRequestsGloas(),
            BuilderIndex = Presets.BuilderIndexSelfBuild,
            BeaconBlockRoot = root,
            ParentBeaconBlockRoot = Hash256.Zero,
        },
        Signature = new BlsSignature(new byte[BlsSignature.Length]),
    };
}
