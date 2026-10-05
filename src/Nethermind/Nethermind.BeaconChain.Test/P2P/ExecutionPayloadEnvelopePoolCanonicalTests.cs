// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.ReqResp;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
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
    public void Omits_a_block_whose_child_builds_on_the_payload_before_it([Values(2UL, 3UL)] ulong count)
    {
        EnvelopeChain chain = new();
        (Hash256 genesis, Hash256 genesisHash) = chain.Put(Base, Hash256.Zero, Hash256.Zero);
        (Hash256 empty, _) = chain.Put(Base + 1, genesis, genesisHash);
        (Hash256 head, _) = chain.Put(Base + 2, empty, genesisHash);
        chain.AddEnvelopes(genesis, empty);
        chain.SetHead(head, Base + 2);

        Assert.That(chain.ServedRoots(Base, count), Is.EqualTo(new[] { genesis }), "the head's bid names the grandparent's payload, so its parent's is EMPTY");
    }

    [TestCase(false, false, TestName = "Withholds_an_empty_head_even_when_its_verified_envelope_is_held(False)")]
    [TestCase(false, true, TestName = "Withholds_an_empty_head_even_when_its_verified_envelope_is_held(True)")]
    [TestCase(true, false, TestName = "Serves_a_full_head_only_when_its_envelope_is_held(False)")]
    [TestCase(true, true, TestName = "Serves_a_full_head_only_when_its_envelope_is_held(True)")]
    public void Head_is_served_only_when_full_and_its_envelope_is_held(bool full, bool held)
    {
        EnvelopeChain chain = new();
        (Hash256 genesis, Hash256 genesisHash) = chain.Put(Base, Hash256.Zero, Hash256.Zero);
        (Hash256 head, _) = chain.Put(Base + 1, genesis, genesisHash);
        chain.AddEnvelopes(genesis);
        if (held)
        {
            chain.AddEnvelopes(head);
        }

        chain.SetHead(head, Base + 1, full);

        Assert.That(chain.ServedRoots(Base, 2), Is.EqualTo(full && held ? new[] { genesis, head } : new[] { genesis }),
            full ? null : "a held envelope does not make the head FULL; only fork choice can");
    }

    [Test]
    public void Full_status_of_another_root_does_not_serve_the_head()
    {
        EnvelopeChain chain = new();
        (Hash256 genesis, Hash256 genesisHash) = chain.Put(Base, Hash256.Zero, Hash256.Zero);
        (Hash256 head, _) = chain.Put(Base + 1, genesis, genesisHash);
        chain.AddEnvelopes(genesis, head);
        chain.SetHead(head, Base + 1);
        chain.Status.Publish(chain.Status.CurrentStatus, genesis);

        Assert.That(chain.ServedRoots(Base, 2), Is.EqualTo(new[] { genesis }), "a FULL verdict for a previous head must not carry over to the new one");
    }

    [Test]
    public void Never_serves_a_sibling_of_a_full_head()
    {
        EnvelopeChain chain = new();
        (Hash256 genesis, Hash256 genesisHash) = chain.Put(Base, Hash256.Zero, Hash256.Zero);
        (Hash256 head, _) = chain.Put(Base + 1, genesis, genesisHash);
        (Hash256 sibling, _) = chain.Put(Base + 1, genesis, genesisHash, salt: 1);
        chain.AddEnvelopes(genesis, head, sibling);
        chain.SetHead(head, Base + 1, full: true);

        Assert.That(chain.ServedRoots(Base, 2), Is.EqualTo(new[] { genesis, head }), "the sibling is not on the head chain");
    }

    [Test]
    public void Serves_the_full_head_it_started_from_when_the_head_moves_during_the_request()
    {
        EnvelopeChain chain = new();
        (Hash256 genesis, Hash256 genesisHash) = chain.Put(Base, Hash256.Zero, Hash256.Zero);
        (Hash256 head, Hash256 headHash) = chain.Put(Base + 1, genesis, genesisHash);
        (Hash256 next, _) = chain.Put(Base + 2, head, headHash);
        HeadAdvancingStatusSource source = new((chain.HeadStatus(head, Base + 1), head), (chain.HeadStatus(next, Base + 2), next));
        ExecutionPayloadEnvelopePool pool = new(store: chain.Store, status: source);
        foreach (Hash256 root in new[] { genesis, head })
        {
            pool.Add(root, chain.Envelope(root));
        }

        Hash256[] served = [.. pool.GetCanonical(Base + 1, 1).Select(static e => e.Message!.BeaconBlockRoot!)];

        Assert.That(served, Is.EqualTo(new[] { head }), "the head and its payload status come from one snapshot, so a head step mid-request cannot withhold the envelope");
    }

    [Test]
    public void Ignores_a_stale_canonical_index_entry_at_a_slot_the_head_chain_skips()
    {
        EnvelopeChain chain = new();
        (Hash256 genesis, Hash256 genesisHash) = chain.Put(Base, Hash256.Zero, Hash256.Zero);
        (Hash256 orphan, _) = chain.Put(Base + 1, genesis, genesisHash);
        (Hash256 onChain, Hash256 onChainHash) = chain.Put(Base + 2, genesis, genesisHash);
        (Hash256 head, _) = chain.Put(Base + 3, onChain, onChainHash);
        chain.Store.SetCanonicalRoot(Base, genesis);
        chain.Store.SetCanonicalRoot(Base + 1, orphan);
        chain.Store.SetCanonicalRoot(Base + 2, onChain);
        chain.AddEnvelopes(genesis, orphan, onChain);
        chain.SetHead(head, Base + 3);

        Assert.That(chain.ServedRoots(Base, 3), Is.EqualTo(new[] { genesis, onChain }), "a reorged-out root the index still names is not served");
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
        (Hash256 second, Hash256 secondHash) = chain.Put(FirstGloasSlot + 1, first, firstHash);
        (Hash256 head, _) = chain.Put(FirstGloasSlot + 2, second, secondHash);
        chain.AddEnvelopes(fulu, first, second);
        chain.SetHead(preGloasHead ? fulu : head, preGloasHead ? FirstGloasSlot - 1 : FirstGloasSlot + 2);

        Assert.That(chain.ServedRoots(FirstGloasSlot - 1, 3), Is.EqualTo(preGloasHead ? [] : new[] { first, second }));
        long before = chain.BlockReads;
        chain.ServedRoots(FirstGloasSlot - 1, 3);
        Assert.That(chain.BlockReads - before, Is.Zero, "a pre-Gloas block that ends the walk is decoded once, not once per request");
    }

    // gloas/p2p-interface.md ExecutionPayloadEnvelopesByRange: the first held envelope must survive a concurrent index update.
    [Test]
    public void Serves_the_first_envelope_when_a_reorg_moves_the_index_under_the_published_head([Values] bool betweenReads)
    {
        SlotReadHookColumnsDb db = new(Base + 2);
        EnvelopeChain chain = new(db: db);
        (Hash256 first, Hash256 firstHash) = chain.Put(Base, Hash256.Zero, Hash256.Zero);
        (Hash256 top, Hash256 topHash) = chain.Put(Base + 1, first, firstHash);
        (Hash256 newTop, Hash256 newTopHash) = chain.Put(Base + 1, first, firstHash, salt: 1);
        chain.AddEnvelopes(first, top, newTop);
        if (betweenReads)
        {
            (Hash256 head, _) = chain.Put(Base + 2, top, topHash);
            (Hash256 newHead, _) = chain.Put(Base + 2, newTop, newTopHash, salt: 1);
            chain.SetHead(head, Base + 2);
            db.OnRead = () => chain.Store.ApplyCanonicalIndexChanges([(Base + 1, newTop), (Base + 2, newHead)], Base + 2);
        }
        else
        {
            chain.SetHead(top, Base + 1, full: true);
            chain.Store.SetCanonicalRoot(Base + 1, newTop);
        }

        Assert.That(chain.ServedRoots(Base, 2), Is.EqualTo(betweenReads ? new[] { first, newTop } : new[] { first }));
    }

    [Test]
    public void Reads_the_range_top_again_when_a_reorg_replaces_it_between_reads()
    {
        SlotReadHookColumnsDb db = new(Base + 1);
        EnvelopeChain chain = new(db: db);
        (Hash256 a, Hash256 aHash) = chain.Put(Base, Hash256.Zero, Hash256.Zero);
        (Hash256 aChild, _) = chain.Put(Base + 1, a, aHash);
        (Hash256 b, Hash256 bHash) = chain.Put(Base, Hash256.Zero, Hash256.Zero, salt: 1);
        (Hash256 bChild, _) = chain.Put(Base + 1, b, bHash, salt: 1);
        chain.AddEnvelopes(a, b);
        chain.SetHead(aChild, Base + 1);
        db.OnRead = () => chain.Store.ApplyCanonicalIndexChanges([(Base, b), (Base + 1, bChild)], Base + 1);

        Assert.That(chain.ServedRoots(Base, 1), Is.EqualTo(new[] { b }), "the second read sees one chain, whose child builds on the payload");
    }

    [Test]
    public void Range_whose_only_block_has_an_unknown_payload_status_is_resource_unavailable()
    {
        EnvelopeChain chain = new();
        (Hash256 first, Hash256 firstHash) = chain.Put(Base, Hash256.Zero, Hash256.Zero);
        (Hash256 top, _) = chain.Put(Base + 1, first, firstHash);
        (Hash256 sibling, _) = chain.Put(Base + 1, first, firstHash, salt: 1);
        chain.AddEnvelopes(sibling);
        chain.SetHead(top, Base + 1, full: true);
        chain.Store.SetCanonicalRoot(Base + 1, sibling);

        Eth2ReqRespException? thrown = Assert.Throws<Eth2ReqRespException>(() => chain.ServedRoots(Base + 1, 1));
        Assert.That(thrown!.ResponseCode, Is.EqualTo(ReqRespFraming.ResponseCode.ResourceUnavailable), "an empty reply would claim the held envelope is off the chain");
    }

    [Test]
    public void Serves_old_envelopes_without_decoding_later_blocks()
    {
        EnvelopeChain chain = new();
        (Hash256 first, Hash256 firstHash) = chain.Put(Base, Hash256.Zero, Hash256.Zero);
        Hash256 root = first;
        Hash256 blockHash = firstHash;
        for (ulong slot = Base + 1; slot <= Base + 8193; slot++)
        {
            (root, blockHash) = chain.Put(slot, root, blockHash);
        }

        chain.AddEnvelopes(first);
        chain.SetHead(root, Base + 8193);
        long before = chain.BlockReads;
        Assert.That(chain.ServedRoots(Base, 1), Is.EqualTo(new[] { first }));
        Assert.That(chain.BlockReads - before, Is.EqualTo(2));
        before = chain.BlockReads;
        chain.ServedRoots(Base, 1);
        Assert.That(chain.BlockReads - before, Is.Zero);
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

internal sealed class HeadAdvancingStatusSource(params (StatusMessageV2 Status, Hash256? FullHeadRoot)[] snapshots) : IBeaconChainStatusSource
{
    private int _reads;

    public (StatusMessageV2 Status, Hash256? FullHeadRoot) CurrentHead => snapshots[Math.Min(_reads++, snapshots.Length - 1)];
    public StatusMessageV2 CurrentStatus => CurrentHead.Status;
    public Hash256 JustifiedRoot => Hash256.Zero;
    public bool ExecutionInSync => true;
}

internal sealed class EnvelopeChain
{
    public static readonly BeaconChainSpec Spec = Sepolia;

    private readonly IColumnsDb<BeaconChainDbColumns> _db;
    private readonly Dictionary<Hash256, (ulong Slot, Hash256 BlockHash)> _blocks = [];

    public EnvelopeChain(bool withStore = true, bool withStatus = true, IColumnsDb<BeaconChainDbColumns>? db = null)
    {
        _db = db ?? new MemColumnsDb<BeaconChainDbColumns>();
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

    // Salt distinguishes blocks at the same slot with the same parent.
    public (Hash256 Root, Hash256 BlockHash) Put(ulong slot, Hash256 parentRoot, Hash256 parentBlockHash, int salt = 0)
    {
        SignedBeaconBlockGloas block = CreateMinimalGloasBlock(slot, parentRoot);
        ExecutionPayloadBid bid = block.Message!.Body!.SignedExecutionPayloadBid!.Message!;
        bid.ParentBlockHash = parentBlockHash;
        bid.BlockHash = Keccak.Compute($"payload {slot} {salt}");
        bid.ExecutionRequestsRoot = SszRoots.HashTreeRoot(new ExecutionRequestsGloas());
        Hash256 root = SszRoots.HashTreeRoot(block.Message);
        Store.PutForkedBlock(root, new ForkedSignedBeaconBlock.OfGloas(block));
        if (salt == 0) Store.SetCanonicalRoot(slot, root);
        _blocks[root] = (slot, bid.BlockHash);
        return (root, bid.BlockHash);
    }

    public Hash256 PutFulu(ulong slot)
    {
        SignedBeaconBlock block = CreateMinimalBlock(slot);
        Hash256 root = SszRoots.HashTreeRoot(block.Message!);
        Store.PutBlock(root, block);
        Store.SetCanonicalRoot(slot, root);
        _blocks[root] = (slot, Hash256.Zero);
        return root;
    }

    public void Corrupt(Hash256 root) => _db.GetColumnDb(BeaconChainDbColumns.Blocks)[root.Bytes] = [0xFF];

    public void AddEnvelopes(params Hash256[] roots)
    {
        foreach (Hash256 root in roots)
        {
            Pool.Add(root, Envelope(root));
        }
    }

    public void SetHead(Hash256 root, ulong slot, bool full = false)
    {
        Store.SetCanonicalRoot(slot, root);
        Status.Publish(HeadStatus(root, slot), full ? root : null);
    }

    public StatusMessageV2 HeadStatus(Hash256 root, ulong slot) => new()
    {
        ForkDigest = Status.CurrentStatus.ForkDigest,
        FinalizedRoot = Hash256.Zero,
        FinalizedEpoch = Status.CurrentStatus.FinalizedEpoch,
        HeadRoot = root,
        HeadSlot = slot,
        EarliestAvailableSlot = Status.CurrentStatus.EarliestAvailableSlot,
    };

    public Hash256[] ServedRoots(ulong startSlot, ulong count) => [.. Pool.GetCanonical(startSlot, count).Select(static e => e.Message!.BeaconBlockRoot!)];

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
