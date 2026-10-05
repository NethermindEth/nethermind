// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.IO;
using Nethermind.BeaconChain.ForkChoice;
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
using Snappier;
using static Nethermind.BeaconChain.Test.Types.SignedBeaconBlockBuilders;

namespace Nethermind.BeaconChain.Test.P2P.Gossip;

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

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(message.Length, Is.EqualTo(compressedLength), "fixture: valid snappy of the exact length");
        Assert.That(validity, Is.EqualTo(MessageValidity.Rejected));
        Assert.That(router.GetDropCount(reason), Is.EqualTo(1));
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

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That((ssz.Length <= MaxSignedAggregateAndProofSizeGloas, message.Length > Eth2MessageId.MaxCompressedLength(MaxSignedAggregateAndProofSizeGloas)), Is.EqualTo((true, true)), "fixture");
        Assert.That(Snappy.DecompressToArray(message), Is.EqualTo(ssz), "fixture: valid snappy");
        AssertVerdict(router, validity, received, null, rejected: false);
    }

    public enum InvalidBlockField { FuluTimestamp, FuluBlobCount, FuluParentSlot, GloasBlobCount, GloasParentSlot, GloasBidParentRoot }

    [Test]
    public void Invalid_block_fields_require_validated_parent_data([Values] InvalidBlockField fault, [Values] bool earlyNextSlot, [Values] bool parentValidated)
    {
        bool gloas = fault is InvalidBlockField.GloasBlobCount or InvalidBlockField.GloasParentSlot or InvalidBlockField.GloasBidParentRoot;
        ulong currentSlot = gloas ? VoteSlot : CurrentSlot;
        ulong slot = currentSlot + (earlyNextSlot ? 1UL : 0UL);
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), gloas ? Sepolia : Spec);
        Hash256 parentRoot = Keccak.Compute("parent");
        ulong parentSlot = fault is InvalidBlockField.FuluParentSlot or InvalidBlockField.GloasParentSlot ? slot : slot - 1;
        SlotClock clock = new(gloas ? Sepolia : Spec, new ManualTimestamper(gloas ? SepoliaSlotStart(currentSlot).AddSeconds(6)
            : DateTime.UnixEpoch.AddSeconds(Spec.GenesisTime + currentSlot * Spec.SecondsPerSlot + 6)));
        CheckpointRef checkpoint = new(0, parentRoot);
        ForkChoiceSnapshotHolder snapshots = new()
        {
            Current = new(checkpoint, checkpoint, Hash256.Zero, parentValidated
                ? [new(parentSlot, parentRoot, null, 0, 0, 0, ExecutionStatus.Valid, Hash256.Zero, PayloadValid: true)] : []),
        };
        ColumnGossipRouter headers = new(gloas ? Sepolia : Spec, clock, LimboLogs.Instance, forkChoice: snapshots);
        GossipRouter router = new(gloas ? Sepolia : Spec, clock, LimboLogs.Instance, store, headers: headers);
        int received = 0;
        router.BeaconBlockReceived += (_, _) => received++;
        byte[] payload;
        if (gloas)
        {
            store.PutForkedBlock(parentRoot, new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(parentSlot)));
            SignedBeaconBlockGloas block = CreateMinimalGloasBlock(slot, parentRoot);
            if (fault == InvalidBlockField.GloasBidParentRoot) block.Message!.Body!.SignedExecutionPayloadBid!.Message!.ParentBlockRoot = Keccak.Compute("other parent");
            if (fault == InvalidBlockField.GloasBlobCount) block.Message!.Body!.SignedExecutionPayloadBid!.Message!.BlobKzgCommitments = Commitments();
            payload = SignedBeaconBlockGloas.Encode(block);
        }
        else
        {
            store.PutBlock(parentRoot, TestChain.CreateBlock(parentSlot, Hash256.Zero));
            SignedBeaconBlock block = TestChain.CreateBlock(slot, parentRoot);
            if (fault == InvalidBlockField.FuluTimestamp) block.Message!.Body!.ExecutionPayload!.Timestamp++;
            if (fault == InvalidBlockField.FuluBlobCount) block.Message!.Body!.BlobKzgCommitments = Commitments();
            payload = SignedBeaconBlock.Encode(block);
        }

        MessageValidity validity = router.Handle(GossipTopics.BeaconBlock, gloas, Snappy.CompressToArray(payload));

        GossipDropReason reason = fault switch
        {
            InvalidBlockField.FuluParentSlot or InvalidBlockField.GloasParentSlot => GossipDropReason.NotAboveParentSlot,
            InvalidBlockField.FuluBlobCount or InvalidBlockField.GloasBlobCount => GossipDropReason.LimitExceeded,
            _ => GossipDropReason.InvalidField,
        };
        if (!parentValidated && earlyNextSlot)
            Assert.That((validity, received), Is.EqualTo((MessageValidity.Ignored, 0)), "an early block waits for its slot before parent validation");
        else
            AssertVerdict(router, validity, received, parentValidated ? reason : null, rejected: parentValidated && !earlyNextSlot);

        static SszKzgCommitment[] Commitments() => Enumerable.Range(0, 100)
            .Select(_ => SszKzgCommitment.FromSpan(new byte[SszKzgCommitment.KzgCommitmentLength])).ToArray();
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

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(store.HasBlock(parentRoot), Is.True, "fixture: the parent is held");
        AssertVerdict(router, validity, received, null, rejected: false);
        Assert.That(router.GetDropCount(GossipDropReason.InvalidSsz), Is.Zero);
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

    // electra p2p beacon_aggregate_and_proof: both REJECTs follow the future-slot IGNORE.
    [TestCase(true, false, false, TestName = "aggregate whose target epoch differs from its slot's")]
    [TestCase(false, true, false, TestName = "aggregate with no participants")]
    [TestCase(true, false, true, TestName = "early next-slot aggregate whose target epoch differs only drops")]
    [TestCase(false, true, true, TestName = "early next-slot aggregate with no participants only drops")]
    public void Aggregate_target_epoch_and_participants_are_checked_after_the_clock(bool wrongTarget, bool noParticipants, bool earlyNextSlot)
    {
        (GossipRouter router, BeaconChainStore store) = CreateRouterWithStore();
        int received = 0;
        router.AggregateAndProofReceived += (_, _) => received++;
        SignedAggregateAndProof aggregate = CreateAggregate(earlyNextSlot ? CurrentSlot + 1 : CurrentSlot);
        store.PutBlock(aggregate.Message!.Aggregate!.Data!.BeaconBlockRoot!, TestChain.CreateBlock(CurrentSlot, Hash256.Zero));
        if (wrongTarget) aggregate.Message!.Aggregate!.Data!.Target!.Epoch++;
        if (noParticipants) aggregate.Message!.Aggregate!.AggregationBits = new BitArray(8);

        MessageValidity validity = router.HandleAggregateAndProof(Snappy.CompressToArray(SignedAggregateAndProof.Encode(aggregate)));

        AssertVerdict(router, validity, received, GossipDropReason.InvalidField, rejected: !earlyNextSlot);
    }

    [Test]
    public async Task Aggregate_unknown_block_precedes_target_and_participant_rejections([Values] bool gloas, [Values] bool wrongTarget)
    {
        (GossipRouter router, _) = gloas ? CreateSepoliaRouterWithStore() : CreateRouterWithStore();
        byte[] payload;
        if (gloas)
        {
            SignedAggregateAndProofGloas aggregate = VoteFor(Keccak.Compute("unknown block"), VoteSlot, 0);
            if (wrongTarget) aggregate.Message!.Aggregate!.Data!.Target!.Epoch++;
            else aggregate.Message!.Aggregate!.AggregationBits!.SetAll(false);
            payload = SignedAggregateAndProofGloas.Encode(aggregate);
        }
        else
        {
            SignedAggregateAndProof aggregate = CreateAggregate(CurrentSlot);
            if (wrongTarget) aggregate.Message!.Aggregate!.Data!.Target!.Epoch++;
            else aggregate.Message!.Aggregate!.AggregationBits!.SetAll(false);
            payload = SignedAggregateAndProof.Encode(aggregate);
        }

        byte[] compressed = Snappy.CompressToArray(payload);
        await AssertDeferredPeerPenaltyAsync(GossipTopics.BeaconAggregateAndProof, compressed, verdict =>
        {
            verdict.Complete(router.Handle(GossipTopics.BeaconAggregateAndProof, gloas, compressed, verdict));
            return Task.CompletedTask;
        }, MessageValidity.Ignored);

        Assert.That(router.GetDropCount(GossipDropReason.UnknownBlock), Is.EqualTo(1), "an unseen block is unavailable, not an invalid field");
    }

    [Test]
    public void Aggregate_failed_block_rejects_after_duplicates_before_committee_and_timing_checks(
        [Values] bool gloas, [Values] bool verified, [Values] bool earlyNextSlot, [Values(0, 1)] int committeeIndex)
    {
        ulong wallSlot = gloas ? VoteSlot : CurrentSlot;
        ulong slot = wallSlot + (earlyNextSlot ? 1UL : 0UL);
        FailedBlockRoots failed = new();
        failed.Add(Hash256.Zero, wallSlot);
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), gloas ? Sepolia : Spec);
        DateTime now = gloas ? SepoliaSlotStart(wallSlot) : DateTime.UnixEpoch.AddSeconds(Spec.GenesisTime + wallSlot * Spec.SecondsPerSlot);
        GossipRouter router = new(gloas ? Sepolia : Spec, new SlotClock(gloas ? Sepolia : Spec, new ManualTimestamper(now.AddSeconds(6))),
            LimboLogs.Instance, store, failedBlocks: failed);
        int received = 0;
        router.AggregateAndProofReceived += (_, _) => received++;
        router.GloasAggregateAndProofReceived += (_, _) => received++;
        SignedAggregateAndProof signed = CreateAggregate(slot);
        AggregateAndProof message = signed.Message!;
        Attestation aggregate = message.Aggregate!;
        aggregate.CommitteeBits!.SetAll(false);
        aggregate.CommitteeBits[committeeIndex] = true;
        if (verified)
            router.MarkAggregateSeen(aggregate.Data!, aggregate.CommitteeBits, aggregate.AggregationBits!, message.AggregatorIndex);

        byte[] payload;
        if (gloas)
        {
            SignedAggregateAndProofGloas signedGloas = VoteFor(Hash256.Zero, slot, 0);
            signedGloas.Message!.Aggregate!.CommitteeBits = aggregate.CommitteeBits;
            payload = SignedAggregateAndProofGloas.Encode(signedGloas);
        }
        else
        {
            payload = SignedAggregateAndProof.Encode(signed);
        }

        MessageValidity validity = router.Handle(GossipTopics.BeaconAggregateAndProof, gloas, Snappy.CompressToArray(payload));

        AssertVerdict(router, validity, received, verified ? GossipDropReason.Duplicate : GossipDropReason.InvalidField, rejected: !verified);
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
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(validity, Is.EqualTo(rejected ? MessageValidity.Rejected : MessageValidity.Ignored), "validity");
        Assert.That(received, Is.EqualTo(reason is null ? 1 : 0), "a message passing every synchronous rule is consumed, a dropped one never");
        if (reason is { } expected)
        {
            Assert.That(router.GetDropCount(expected), Is.EqualTo(1), "drop reason");
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
            }

            stream.WriteByte(0);
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
