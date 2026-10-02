// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.IO;
using System.Linq;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.SszRest;
using Nethermind.Network.Libp2p;
using NSubstitute;
using NUnit.Framework;
using Snappier;
using static Nethermind.BeaconChain.Test.Types.SignedBeaconBlockBuilders;

namespace Nethermind.BeaconChain.Test.P2P.Gossip;

/// <summary>The size, block, aggregate and attester slashing rules that return a verdict without beacon state.</summary>
public partial class GossipRouterTests
{
    private const int MaxSignedAggregateAndProofSizeGloas = 16829;

    // phase0 p2p "Gossipsub size limits": the compressed payload must not exceed max_compressed_len(MAX_PAYLOAD_SIZE) = 12233418 on any topic;
    // 19666 is one over max_compressed_len(16829), the Gloas aggregate's type bound, which caps only the uncompressed size.
    [TestCase(GossipTopics.BeaconAggregateAndProof, true, 19666, GossipDropReason.InvalidSsz, TestName = "Gloas aggregate over max_compressed_len of its type bound is decompressed")]
    [TestCase(GossipTopics.BeaconAggregateAndProof, true, 12233419, GossipDropReason.Oversized, TestName = "Gloas aggregate one byte over max_compressed_len(MAX_PAYLOAD_SIZE)")]
    [TestCase(GossipTopics.BeaconBlock, false, 12233418, GossipDropReason.InvalidSsz, TestName = "Fulu block at max_compressed_len(MAX_PAYLOAD_SIZE) is decompressed")]
    [TestCase(GossipTopics.BeaconBlock, false, 12233419, GossipDropReason.Oversized, TestName = "Fulu block one byte over max_compressed_len(MAX_PAYLOAD_SIZE)")]
    public void Compressed_payload_over_max_compressed_len_is_rejected_before_decompression(string name, bool gloasTopic, int compressedLength, GossipDropReason reason)
    {
        GossipRouter router = CreateRouter();
        byte[] message = SnappyOfExactLength(compressedLength, out _);

        MessageValidity validity = router.Handle(name, gloasTopic, message);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(message.Length, Is.EqualTo(compressedLength), "fixture: valid snappy of the exact length");
            Assert.That(validity, Is.EqualTo(MessageValidity.Rejected));
            Assert.That(router.GetDropCount(reason), Is.EqualTo(1));
        }
    }

    // A valid aggregate encoded with 6-byte literals exceeds max_compressed_len(16829) yet stays far under max_compressed_len(MAX_PAYLOAD_SIZE).
    [Test]
    public void Valid_Gloas_aggregate_compressed_past_max_compressed_len_of_its_type_bound_is_consumed()
    {
        GossipRouter router = new(Sepolia, new SlotClock(Sepolia, new ManualTimestamper(SepoliaSlotStart(FirstGloasSlot + 1).AddSeconds(6))), LimboLogs.Instance);
        int received = 0;
        router.GloasAggregateAndProofReceived += (_, _) => received++;
        SignedAggregateAndProofGloas aggregate = GossipMessageValidatorTests.GloasAggregate(FirstGloasSlot + 1);
        aggregate.Message!.Aggregate!.AggregationBits = new BitArray(8 * 4096) { [0] = true };
        byte[] ssz = SignedAggregateAndProofGloas.Encode(aggregate);
        byte[] message = SnappyOfFourByteLengthLiterals(ssz);

        MessageValidity validity = router.Handle(GossipTopics.BeaconAggregateAndProof, gloasTopic: true, message);

        using (Assert.EnterMultipleScope())
        {
            Assert.That((ssz.Length <= MaxSignedAggregateAndProofSizeGloas, message.Length > Eth2MessageId.MaxCompressedLength(MaxSignedAggregateAndProofSizeGloas)), Is.EqualTo((true, true)), "fixture");
            Assert.That(Snappy.DecompressToArray(message), Is.EqualTo(ssz), "fixture: valid snappy");
            AssertVerdict(router, validity, received, null, rejected: false);
        }
    }

    // bellatrix p2p beacon_block: execution_payload.timestamp == compute_time_at_slot(state, block.slot).
    [TestCase(0L, false, null, TestName = "Fulu block with the slot's payload timestamp is consumed")]
    [TestCase(1L, false, GossipDropReason.InvalidField, TestName = "Fulu block with a payload timestamp one second late")]
    [TestCase(-1L, false, GossipDropReason.InvalidField, TestName = "Fulu block with a payload timestamp one second early")]
    [TestCase(1L, true, GossipDropReason.InvalidField, TestName = "early next-slot Fulu block with a wrong payload timestamp only drops")]
    public void Fulu_block_payload_timestamp_must_match_its_slot(long timestampOffset, bool earlyNextSlot, GossipDropReason? reason)
    {
        GossipRouter router = CreateRouter();
        int received = 0;
        router.BeaconBlockReceived += (_, _) => received++;
        ulong slot = earlyNextSlot ? CurrentSlot + 1 : CurrentSlot;
        SignedBeaconBlock block = TestChain.CreateBlock(slot, Hash256.Zero);
        block.Message!.Body!.ExecutionPayload!.Timestamp = (ulong)((long)(Spec.GenesisTime + slot * Spec.SecondsPerSlot) + timestampOffset);

        MessageValidity validity = router.HandleBeaconBlock(Snappy.CompressToArray(SignedBeaconBlock.Encode(block)));

        AssertVerdict(router, validity, received, reason, rejected: reason is not null && !earlyNextSlot);
    }

    // gloas p2p beacon_block: the bid-parent REJECT follows the parent-not-seen IGNORE, so it applies only to a held parent.
    [TestCase(true, false, true, TestName = "bid naming another parent than a held block parent")]
    [TestCase(false, false, false, TestName = "bid naming another parent than an unseen block parent only drops")]
    [TestCase(true, true, false, TestName = "early next-slot block whose bid names another parent only drops")]
    public void Gloas_bid_parent_mismatch_is_rejected_once_the_parent_is_held(bool parentHeld, bool earlyNextSlot, bool rejected)
    {
        ulong wallSlot = FirstGloasSlot + 1;
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), Sepolia);
        Hash256 parentRoot = Keccak.Compute("parent");
        if (parentHeld) store.PutForkedBlock(parentRoot, new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(wallSlot - 1)));
        GossipRouter router = new(Sepolia, new SlotClock(Sepolia, new ManualTimestamper(SepoliaSlotStart(wallSlot).AddSeconds(6))), LimboLogs.Instance, store);
        int received = 0;
        router.BeaconBlockReceived += (_, _) => received++;
        SignedBeaconBlockGloas block = CreateMinimalGloasBlock(earlyNextSlot ? wallSlot + 1 : wallSlot, parentRoot, bidParentRoot: Keccak.Compute("other parent"));

        MessageValidity validity = router.Handle(GossipTopics.BeaconBlock, gloasTopic: true, Snappy.CompressToArray(SignedBeaconBlockGloas.Encode(block)));

        AssertVerdict(router, validity, received, GossipDropReason.InvalidField, rejected);
    }

    // phase0 p2p beacon_block: [REJECT] the block is from a higher slot than its parent; a held parent answers it without state.
    [TestCase(1L, false, null, TestName = "block one slot above its held parent is consumed")]
    [TestCase(0L, false, GossipDropReason.NotAboveParentSlot, TestName = "block at its held parent's slot")]
    [TestCase(-1L, false, GossipDropReason.NotAboveParentSlot, TestName = "block below its held parent's slot")]
    [TestCase(0L, true, GossipDropReason.NotAboveParentSlot, TestName = "early next-slot block at its held parent's slot only drops")]
    public void Block_must_be_above_the_slot_of_its_held_parent(long slotsAboveParent, bool earlyNextSlot, GossipDropReason? reason)
    {
        ulong slot = earlyNextSlot ? CurrentSlot + 1 : CurrentSlot;
        (GossipRouter router, BeaconChainStore store) = CreateRouterWithStore();
        int received = 0;
        router.BeaconBlockReceived += (_, _) => received++;
        Hash256 parentRoot = Keccak.Compute("parent");
        store.PutBlock(parentRoot, TestChain.CreateBlock((ulong)((long)slot - slotsAboveParent), Hash256.Zero));

        MessageValidity validity = router.HandleBeaconBlock(Snappy.CompressToArray(SignedBeaconBlock.Encode(TestChain.CreateBlock(slot, parentRoot))));

        AssertVerdict(router, validity, received, reason, rejected: reason is not null && !earlyNextSlot);
    }

    // A parent's slot costs a store decode on the network thread, so reads are bounded per slot and a read slot is kept.
    [Test]
    public void Parent_slot_reads_past_the_decode_budget_skip_the_rule_but_a_read_parent_still_applies_it()
    {
        (GossipRouter router, BeaconChainStore store) = CreateRouterWithStore();
        int received = 0;
        router.BeaconBlockReceived += (_, _) => received++;
        Hash256[] parents = new Hash256[GossipRouter.ParentSlotReadsPerSlot + 1];
        for (int i = 0; i < parents.Length; i++)
        {
            parents[i] = Keccak.Compute($"parent {i}");
            store.PutBlock(parents[i], TestChain.CreateBlock(CurrentSlot, Hash256.Zero));
        }

        MessageValidity[] verdicts = new MessageValidity[parents.Length];
        for (int i = 0; i < parents.Length; i++)
        {
            verdicts[i] = router.HandleBeaconBlock(Snappy.CompressToArray(SignedBeaconBlock.Encode(TestChain.CreateBlock(CurrentSlot, parents[i]))));
        }

        SignedBeaconBlock sibling = TestChain.CreateBlock(CurrentSlot, parents[0]);
        sibling.Message!.ProposerIndex++;
        MessageValidity cached = router.HandleBeaconBlock(Snappy.CompressToArray(SignedBeaconBlock.Encode(sibling)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(verdicts[..^1], Is.All.EqualTo(MessageValidity.Rejected), "within the budget");
            Assert.That((verdicts[^1], received), Is.EqualTo((MessageValidity.Ignored, 1)), "past the budget the block is consumed for the import pipeline");
            Assert.That(cached, Is.EqualTo(MessageValidity.Rejected), "a parent already read needs no decode");
            Assert.That(router.GetDropCount(GossipDropReason.NotAboveParentSlot), Is.EqualTo(GossipRouter.ParentSlotReadsPerSlot + 1));
        }
    }

    // Blocks naming held parents and envelopes naming held blocks each decode within their own per-slot budget, so neither starves the other.
    [TestCase(true, TestName = "envelopes spending their decode budget leave the parent-slot rule applied")]
    [TestCase(false, TestName = "blocks spending the parent-slot budget leave envelopes decoded")]
    public void Parent_slot_reads_and_envelope_decodes_have_separate_budgets(bool envelopesFirst)
    {
        EnvelopeFixture fixture = new(withStore: true);
        ulong slot = EnvelopeFixture.WallSlot;
        Hash256[] envelopeBlocks = [.. Enumerable.Range(0, GossipRouter.StoreDecodesPerSlot).Select(i => fixture.PutGloasBlock(slot, parentRoot: Keccak.Compute($"envelope block parent {i}")))];
        Hash256[] parents = [.. Enumerable.Range(0, GossipRouter.ParentSlotReadsPerSlot).Select(i => fixture.PutGloasBlock(slot, parentRoot: Keccak.Compute($"held parent {i}")))];
        Hash256 lastEnvelopeBlock = fixture.PutGloasBlock(slot, parentRoot: Keccak.Compute("last envelope block parent"));
        Hash256 lastParent = fixture.PutGloasBlock(slot, parentRoot: Keccak.Compute("last held parent"));

        if (envelopesFirst)
        {
            SpendEnvelopeBudget();
        }
        else
        {
            SpendParentSlotBudget();
        }

        MessageValidity probe = envelopesFirst ? HandleBlock(lastParent) : fixture.Handle(fixture.Envelope(lastEnvelopeBlock, builderIndex: EnvelopeFixture.BuilderIndex + 1));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(probe, Is.EqualTo(MessageValidity.Rejected), "the other budget is still whole");
            Assert.That(fixture.Router.GetDropCount(GossipDropReason.StoreDecodeBudgetSpent), Is.Zero);
            Assert.That(fixture.Router.GetDropCount(envelopesFirst ? GossipDropReason.NotAboveParentSlot : GossipDropReason.InvalidField), Is.EqualTo(1));
        }

        void SpendEnvelopeBudget()
        {
            foreach (Hash256 root in envelopeBlocks)
            {
                fixture.Handle(fixture.Envelope(root, builderIndex: EnvelopeFixture.BuilderIndex + 1));
            }
        }

        void SpendParentSlotBudget()
        {
            foreach (Hash256 parent in parents)
            {
                HandleBlock(parent);
            }
        }

        MessageValidity HandleBlock(Hash256 parent) =>
            fixture.Router.Handle(GossipTopics.BeaconBlock, gloasTopic: true, Snappy.CompressToArray(SignedBeaconBlockGloas.Encode(CreateMinimalGloasBlock(slot, parent))));
    }

    // A store that fails to read a held parent leaves the parent-slot rule to the state transition; the block is not an SSZ REJECT.
    [Test]
    public void Parent_whose_store_read_throws_skips_the_parent_slot_rule()
    {
        MemDb blocks = new ThrowingOnReadDb();
        IColumnsDb<BeaconChainDbColumns> db = Substitute.For<IColumnsDb<BeaconChainDbColumns>>();
        db.GetColumnDb(Arg.Any<BeaconChainDbColumns>()).Returns(static _ => new MemDb());
        db.GetColumnDb(BeaconChainDbColumns.Blocks).Returns(blocks);
        BeaconChainStore store = new(db, Spec);
        Hash256 parentRoot = Keccak.Compute("parent");
        blocks.Set(parentRoot.Bytes, [0x01]);
        DateTime now = DateTime.UnixEpoch.AddSeconds(Spec.GenesisTime + CurrentSlot * Spec.SecondsPerSlot).AddSeconds(6);
        GossipRouter router = new(Spec, new SlotClock(Spec, new ManualTimestamper(now)), LimboLogs.Instance, store);
        int received = 0;
        router.BeaconBlockReceived += (_, _) => received++;

        MessageValidity validity = router.HandleBeaconBlock(Snappy.CompressToArray(SignedBeaconBlock.Encode(TestChain.CreateBlock(CurrentSlot, parentRoot))));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.HasBlock(parentRoot), Is.True, "fixture: the parent is held");
            AssertVerdict(router, validity, received, null, rejected: false);
            Assert.That(router.GetDropCount(GossipDropReason.InvalidSsz), Is.Zero);
        }
    }

    private sealed class ThrowingOnReadDb : MemDb
    {
        public override byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None) => throw new IOException("the disk read failed");
    }

    private static (GossipRouter Router, BeaconChainStore Store) CreateRouterWithStore()
    {
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), Spec);
        DateTime now = DateTime.UnixEpoch.AddSeconds(Spec.GenesisTime + CurrentSlot * Spec.SecondsPerSlot).AddSeconds(6);
        return (new GossipRouter(Spec, new SlotClock(Spec, new ManualTimestamper(now)), LimboLogs.Instance, store), store);
    }

    [TestCase(false, TestName = "Fulu block over the blob limit")]
    [TestCase(true, TestName = "early next-slot Fulu block over the blob limit only drops")]
    public void Fulu_block_over_the_blob_limit_of_its_epoch(bool earlyNextSlot)
    {
        GossipRouter router = CreateRouter();
        int received = 0;
        router.BeaconBlockReceived += (_, _) => received++;
        ulong slot = earlyNextSlot ? CurrentSlot + 1 : CurrentSlot;
        SignedBeaconBlock block = TestChain.CreateBlock(slot, Hash256.Zero);
        int maxBlobs = (int)Spec.GetBlobParameters(Spec.GetEpoch(slot))!.Value.MaxBlobsPerBlock;
        block.Message!.Body!.BlobKzgCommitments = new SszKzgCommitment[maxBlobs + 1];
        for (int i = 0; i <= maxBlobs; i++)
        {
            block.Message.Body.BlobKzgCommitments[i] = SszKzgCommitment.FromSpan(new byte[SszKzgCommitment.KzgCommitmentLength]);
        }

        MessageValidity validity = router.HandleBeaconBlock(Snappy.CompressToArray(SignedBeaconBlock.Encode(block)));

        AssertVerdict(router, validity, received, GossipDropReason.LimitExceeded, rejected: !earlyNextSlot);
    }

    // electra p2p beacon_aggregate_and_proof: both REJECTs follow the future-slot IGNORE.
    [TestCase(true, false, false, TestName = "aggregate whose target epoch differs from its slot's")]
    [TestCase(false, true, false, TestName = "aggregate with no participants")]
    [TestCase(true, false, true, TestName = "early next-slot aggregate whose target epoch differs only drops")]
    [TestCase(false, true, true, TestName = "early next-slot aggregate with no participants only drops")]
    public void Aggregate_target_epoch_and_participants_are_checked_after_the_clock(bool wrongTarget, bool noParticipants, bool earlyNextSlot)
    {
        GossipRouter router = CreateRouter();
        int received = 0;
        router.AggregateAndProofReceived += (_, _) => received++;
        SignedAggregateAndProof aggregate = CreateAggregate(earlyNextSlot ? CurrentSlot + 1 : CurrentSlot);
        if (wrongTarget) aggregate.Message!.Aggregate!.Data!.Target!.Epoch++;
        if (noParticipants) aggregate.Message!.Aggregate!.AggregationBits = new BitArray(8);

        MessageValidity validity = router.HandleAggregateAndProof(Snappy.CompressToArray(SignedAggregateAndProof.Encode(aggregate)));

        AssertVerdict(router, validity, received, GossipDropReason.InvalidField, rejected: !earlyNextSlot);
    }

    // phase0 p2p attester_slashing: IGNORE when every intersecting index is seen, then REJECT non-slashable data.
    [TestCase(new ulong[] { 1, 2 }, new ulong[] { 2, 3 }, true, new ulong[] { 2 }, GossipDropReason.Duplicate, false, TestName = "slashing whose only intersecting index is seen")]
    [TestCase(new ulong[] { 1, 2 }, new ulong[] { 2, 3 }, false, new ulong[] { 2 }, GossipDropReason.Duplicate, false, TestName = "non-slashable data whose intersecting indices are all seen")]
    [TestCase(new ulong[] { 1, 2, 5 }, new ulong[] { 2, 3, 5 }, false, new ulong[] { 2 }, GossipDropReason.InvalidField, true, TestName = "non-slashable data with an intersecting index not seen")]
    [TestCase(new ulong[] { 1, 2, 5 }, new ulong[] { 2, 3, 5 }, true, new ulong[] { 2 }, null, false, TestName = "slashing with an intersecting index not seen is consumed")]
    [TestCase(new ulong[] { 1, 2 }, new ulong[] { 3, 4 }, true, new ulong[0], GossipDropReason.InvalidField, false, TestName = "slashing with no intersecting index")]
    public void Attester_slashing_is_ignored_once_every_intersecting_index_is_seen(ulong[] indices1, ulong[] indices2, bool slashable, ulong[] seen, GossipDropReason? reason, bool rejected)
    {
        GossipRouter router = CreateRouter();
        int received = 0;
        router.AttesterSlashingReceived += (_, _) => received++;
        router.MarkSlashedIndicesSeen(seen);
        IndexedAttestation first = CreateIndexedAttestation(1, 4);
        IndexedAttestation second = slashable ? CreateIndexedAttestation(2, 3) : CreateIndexedAttestation(5, 6);
        first.AttestingIndices = indices1;
        second.AttestingIndices = indices2;

        MessageValidity validity = router.HandleAttesterSlashing(Snappy.CompressToArray(AttesterSlashing.Encode(new AttesterSlashing { Attestation1 = first, Attestation2 = second })));

        AssertVerdict(router, validity, received, reason, rejected);
    }

    private static void AssertVerdict(GossipRouter router, MessageValidity validity, int received, GossipDropReason? reason, bool rejected)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(validity, Is.EqualTo(rejected ? MessageValidity.Rejected : MessageValidity.Ignored), "validity");
            Assert.That(received, Is.EqualTo(reason is null ? 1 : 0), "a message passing every synchronous rule is consumed, a dropped one never");
            if (reason is { } expected)
            {
                Assert.That(router.GetDropCount(expected), Is.EqualTo(1), "drop reason");
            }
        }
    }

    /// <summary>Valid snappy of <paramref name="payload"/> as one-byte literals in the 6-byte form: tag 0xFC, a 4-byte length, the byte.</summary>
    private static byte[] SnappyOfFourByteLengthLiterals(byte[] payload)
    {
        using MemoryStream stream = new();
        for (uint value = (uint)payload.Length; ; value >>= 7)
        {
            if (value < 0x80)
            {
                stream.WriteByte((byte)value);
                break;
            }

            stream.WriteByte((byte)(value | 0x80));
        }

        foreach (byte b in payload)
        {
            stream.Write([0xFC, 0, 0, 0, 0, b]);
        }

        return stream.ToArray();
    }

    /// <summary>Valid snappy of exactly <paramref name="compressedLength"/> bytes built from one-byte literals, in the short and the long tag form.</summary>
    private static byte[] SnappyOfExactLength(int compressedLength, out int uncompressedLength)
    {
        // A one-byte literal costs 2 bytes as tag 0x00 and 3 bytes as the long tag 0xF0 with a length byte.
        int n = 0;
        int header = 1;
        for (int i = 0; i < 2; i++)
        {
            n = (compressedLength - header + 2) / 3;
            header = VarintLength((uint)n);
        }

        int longLiterals = compressedLength - header - 2 * n;
        using MemoryStream stream = new();
        for (uint value = (uint)n; ; value >>= 7)
        {
            if (value < 0x80)
            {
                stream.WriteByte((byte)value);
                break;
            }

            stream.WriteByte((byte)(value | 0x80));
        }

        for (int i = 0; i < n; i++)
        {
            if (i < longLiterals)
            {
                stream.WriteByte(0xF0);
                stream.WriteByte(0);
            }
            else
            {
                stream.WriteByte(0);
            }

            stream.WriteByte(0);
        }

        byte[] message = stream.ToArray();
        uncompressedLength = Snappy.GetUncompressedLength(message);
        return message;

        static int VarintLength(uint value)
        {
            int length = 1;
            for (; value >= 0x80; value >>= 7)
            {
                length++;
            }

            return length;
        }
    }
}
