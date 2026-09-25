// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Google.Protobuf;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.SszRest;
using NUnit.Framework;
using Snappier;
using static Nethermind.BeaconChain.Test.Types.SignedBeaconBlockBuilders;

namespace Nethermind.BeaconChain.Test.P2P.Gossip;

// The pubsub library raises topic events only for Accepted and never redelivers an ignored id, so a message that
// passes must be consumed by the validator itself; Rejected is only allowed ahead of every store or state check.
public class GossipMessageValidatorTests
{
    private static readonly ulong WallSlot = FirstGloasSlot + 1;
    private static readonly ulong FuluSlot = FirstGloasSlot - 1;
    private static readonly ulong FinalizedEpoch = Sepolia.GloasForkEpoch - 2;
    private static readonly byte[] GloasDigest = ForkDigest.Compute(Sepolia, Sepolia.GloasForkEpoch);
    private const long MaximumGossipClockDisparityMs = GossipRouter.MaximumGossipClockDisparityMs;
    private static readonly byte[] FuluDigest = ForkDigest.Compute(Sepolia, Sepolia.GloasForkEpoch - 1);

    private static IEnumerable<TestCaseData> Cases()
    {
        byte[] validBlock = Encode(GloasBlock());

        yield return Case("signed message", Topic(GloasDigest, GossipTopics.BeaconBlock), validBlock, MessageValidity.Rejected, null, GossipDropReason.SignedMessage, signed: true);
        yield return Case("unparseable topic", "/eth2/beacon_block", validBlock, MessageValidity.Rejected, null, GossipDropReason.UnknownTopic);
        yield return Case("topic whose prefix and suffix overlap", "/eth2/ssz_snappy", validBlock, MessageValidity.Rejected, null, GossipDropReason.UnknownTopic);
        yield return Case("unknown eth2 topic name", Topic(GloasDigest, "beacon_blocks"), validBlock, MessageValidity.Rejected, null, GossipDropReason.UnknownTopic);
        yield return Case("eth2 topic this node does not subscribe", Topic(GloasDigest, GossipTopics.VoluntaryExit),
            Compress(SignedVoluntaryExit.Encode(new SignedVoluntaryExit { Message = new VoluntaryExit { Epoch = 1, ValidatorIndex = 2 } })), MessageValidity.Ignored, null, GossipDropReason.UnknownTopic);
        yield return Case("digest outside the transition window", Topic(ForkDigest.Compute(Sepolia, 0), GossipTopics.BeaconBlock), validBlock, MessageValidity.Ignored, null, GossipDropReason.UnknownTopic);
        yield return Case("Gloas-only topic on a Fulu digest", Topic(FuluDigest, GossipTopics.ExecutionPayload), Encode(Envelope()), MessageValidity.Ignored, null, GossipDropReason.UnknownTopic);
        yield return Case("invalid snappy", Topic(GloasDigest, GossipTopics.BeaconBlock), [0xff, 0xff, 0xff, 0xff], MessageValidity.Rejected, null, GossipDropReason.InvalidSnappy);
        yield return Case("Gloas aggregate one byte over its size bound", Topic(GloasDigest, GossipTopics.BeaconAggregateAndProof), Compress(new byte[16830]), MessageValidity.Rejected, null, GossipDropReason.Oversized);
        yield return Case("Gloas aggregate at its size bound reaches decoding", Topic(GloasDigest, GossipTopics.BeaconAggregateAndProof), Compress(new byte[16829]), MessageValidity.Rejected, null, GossipDropReason.InvalidSsz);
        yield return Case("Gloas attester slashing one byte over its size bound", Topic(GloasDigest, GossipTopics.AttesterSlashing), Compress(new byte[2097617]), MessageValidity.Rejected, null, GossipDropReason.Oversized);
        yield return Case("Fulu block on a Gloas topic is the wrong type", Topic(GloasDigest, GossipTopics.BeaconBlock), Compress(SignedBeaconBlock.Encode(CreateMinimalBlock(FuluSlot))), MessageValidity.Rejected, null, GossipDropReason.InvalidSsz);

        yield return Case("Gloas block is consumed", Topic(GloasDigest, GossipTopics.BeaconBlock), validBlock, MessageValidity.Ignored, typeof(ForkedSignedBeaconBlock.OfGloas), null);
        yield return Case("Gloas block over a body operation limit", Topic(GloasDigest, GossipTopics.BeaconBlock),
            Encode(GloasBlock(mutate: static b => b.Message!.Body!.VoluntaryExits = [.. Enumerable.Range(0, Presets.MaxVoluntaryExits + 1).Select(static i => Exit((ulong)i))])),
            MessageValidity.Rejected, null, GossipDropReason.LimitExceeded);
        yield return Case("Gloas block over a parent execution request limit", Topic(GloasDigest, GossipTopics.BeaconBlock),
            Encode(GloasBlock(mutate: static b => b.Message!.Body!.ParentExecutionRequests = RequestsOverLimit())),
            MessageValidity.Rejected, null, GossipDropReason.LimitExceeded);
        yield return Case("Gloas block two slots ahead", Topic(GloasDigest, GossipTopics.BeaconBlock), Encode(GloasBlock(WallSlot + 2)), MessageValidity.Ignored, null, GossipDropReason.FutureSlot);
        yield return Case("future block IGNORE precedes its limit REJECT", Topic(GloasDigest, GossipTopics.BeaconBlock),
            Encode(GloasBlock(WallSlot + 2, static b => b.Message!.Body!.ParentExecutionRequests = RequestsOverLimit())),
            MessageValidity.Ignored, null, GossipDropReason.FutureSlot);
        yield return Case("early next-slot block over a limit only drops", Topic(GloasDigest, GossipTopics.BeaconBlock),
            Encode(GloasBlock(WallSlot + 1, static b => b.Message!.Body!.ParentExecutionRequests = RequestsOverLimit())),
            MessageValidity.Ignored, null, GossipDropReason.LimitExceeded);
        yield return Case("block at the finalized start slot", Topic(FuluDigest, GossipTopics.BeaconBlock),
            Compress(SignedBeaconBlock.Encode(CreateMinimalBlock(FinalizedEpoch * Sepolia.SlotsPerEpoch))), MessageValidity.Ignored, null, GossipDropReason.BeforeFinalized);
        yield return Case("blob count over the limit follows store checks, so only drops", Topic(GloasDigest, GossipTopics.BeaconBlock),
            Encode(GloasBlock(mutate: static b => b.Message!.Body!.SignedExecutionPayloadBid!.Message!.BlobKzgCommitments = Commitments(100))),
            MessageValidity.Ignored, null, GossipDropReason.LimitExceeded);
        yield return Case("bid parent root differing from the block's only drops", Topic(GloasDigest, GossipTopics.BeaconBlock),
            Encode(GloasBlock(mutate: static b => b.Message!.Body!.SignedExecutionPayloadBid!.Message!.ParentBlockRoot = Keccak.OfAnEmptyString)),
            MessageValidity.Ignored, null, GossipDropReason.InvalidField);
        yield return Case("Gloas-shaped block at a Fulu slot only drops", Topic(GloasDigest, GossipTopics.BeaconBlock), Encode(GloasBlock(FuluSlot)), MessageValidity.Ignored, null, GossipDropReason.InvalidField);
        yield return Case("Fulu block is consumed", Topic(FuluDigest, GossipTopics.BeaconBlock), Compress(SignedBeaconBlock.Encode(CreateMinimalBlock(FuluSlot))), MessageValidity.Ignored, typeof(ForkedSignedBeaconBlock.OfFulu), null);
        yield return Case("Fulu blob count over the limit only drops", Topic(FuluDigest, GossipTopics.BeaconBlock), Encode(FuluBlock(blobs: 100)), MessageValidity.Ignored, null, GossipDropReason.LimitExceeded);

        yield return Case("Gloas aggregate is consumed", Topic(GloasDigest, GossipTopics.BeaconAggregateAndProof), Encode(GloasAggregate()), MessageValidity.Ignored, typeof(SignedAggregateAndProofGloas), null);
        yield return Case("Gloas aggregate voting payload present is consumed", Topic(GloasDigest, GossipTopics.BeaconAggregateAndProof), Encode(GloasAggregate(index: 1)), MessageValidity.Ignored, typeof(SignedAggregateAndProofGloas), null);
        yield return Case("Gloas aggregate with data index 2", Topic(GloasDigest, GossipTopics.BeaconAggregateAndProof), Encode(GloasAggregate(index: 2)), MessageValidity.Rejected, null, GossipDropReason.InvalidField);
        yield return Case("Gloas aggregate naming two committees", Topic(GloasDigest, GossipTopics.BeaconAggregateAndProof), Encode(GloasAggregate(committees: 2)), MessageValidity.Rejected, null, GossipDropReason.InvalidField);
        yield return Case("Gloas aggregate naming no committee", Topic(GloasDigest, GossipTopics.BeaconAggregateAndProof), Encode(GloasAggregate(committees: 0)), MessageValidity.Rejected, null, GossipDropReason.InvalidField);
        yield return Case("aggregate from two epochs ago", Topic(GloasDigest, GossipTopics.BeaconAggregateAndProof), Encode(GloasAggregate(slot: FirstGloasSlot - 2 * Sepolia.SlotsPerEpoch)), MessageValidity.Ignored, null, GossipDropReason.StaleSlot);
        yield return Case("aggregate target epoch mismatch follows store checks, so only drops", Topic(GloasDigest, GossipTopics.BeaconAggregateAndProof), Encode(GloasAggregate(targetEpoch: 1)), MessageValidity.Ignored, null, GossipDropReason.InvalidField);
        yield return Case("aggregate without participants only drops", Topic(GloasDigest, GossipTopics.BeaconAggregateAndProof), Encode(GloasAggregate(participants: 0)), MessageValidity.Ignored, null, GossipDropReason.InvalidField);
        yield return Case("Fulu aggregate is consumed", Topic(FuluDigest, GossipTopics.BeaconAggregateAndProof), Encode(FuluAggregate(index: 0)), MessageValidity.Ignored, typeof(SignedAggregateAndProof), null);
        yield return Case("Fulu aggregate with data index 1", Topic(FuluDigest, GossipTopics.BeaconAggregateAndProof), Encode(FuluAggregate(index: 1)), MessageValidity.Rejected, null, GossipDropReason.InvalidField);

        yield return Case("Gloas attester slashing is consumed", Topic(GloasDigest, GossipTopics.AttesterSlashing), Encode(GloasSlashing([1, 2], [2, 3], secondSource: 2, secondTarget: 3)), MessageValidity.Ignored, typeof(AttesterSlashingGloas), null);
        yield return Case("attester slashing of non-slashable data is only dropped", Topic(GloasDigest, GossipTopics.AttesterSlashing), Encode(GloasSlashing([1, 2], [2, 3], secondSource: 5, secondTarget: 6)), MessageValidity.Ignored, null, GossipDropReason.InvalidField);
        yield return Case("attester slashing with no common index", Topic(GloasDigest, GossipTopics.AttesterSlashing), Encode(GloasSlashing([1, 2], [3, 4], secondSource: 2, secondTarget: 3)), MessageValidity.Ignored, null, GossipDropReason.InvalidField);
        yield return Case("no common index is dropped whatever the attestation data", Topic(GloasDigest, GossipTopics.AttesterSlashing), Encode(GloasSlashing([1, 2], [3, 4], secondSource: 5, secondTarget: 6)), MessageValidity.Ignored, null, GossipDropReason.InvalidField);
        yield return Case("Fulu attester slashing is consumed", Topic(FuluDigest, GossipTopics.AttesterSlashing), Encode(FuluSlashing([1, 2], [2, 3], secondSource: 2, secondTarget: 3)), MessageValidity.Ignored, typeof(AttesterSlashing), null);
        yield return Case("Fulu attester slashing of non-slashable data is only dropped", Topic(FuluDigest, GossipTopics.AttesterSlashing), Encode(FuluSlashing([1, 2], [2, 3], secondSource: 5, secondTarget: 6)), MessageValidity.Ignored, null, GossipDropReason.InvalidField);
        yield return Case("Fulu attester slashing with no common index", Topic(FuluDigest, GossipTopics.AttesterSlashing), Encode(FuluSlashing([1, 2], [3, 4], secondSource: 5, secondTarget: 6)), MessageValidity.Ignored, null, GossipDropReason.InvalidField);

        yield return Case("execution payload envelope is consumed", Topic(GloasDigest, GossipTopics.ExecutionPayload), Encode(Envelope()), MessageValidity.Ignored, typeof(SignedExecutionPayloadEnvelope), null);
        yield return Case("envelope over an execution request limit", Topic(GloasDigest, GossipTopics.ExecutionPayload), Encode(Envelope(requests: RequestsOverLimit())), MessageValidity.Rejected, null, GossipDropReason.LimitExceeded);
        yield return Case("envelope below the finalized slot", Topic(GloasDigest, GossipTopics.ExecutionPayload), Encode(Envelope(slot: FinalizedEpoch * Sepolia.SlotsPerEpoch - 1)), MessageValidity.Ignored, null, GossipDropReason.BeforeFinalized);
        yield return Case("envelope at the finalized start slot is consumed", Topic(GloasDigest, GossipTopics.ExecutionPayload), Encode(Envelope(slot: FinalizedEpoch * Sepolia.SlotsPerEpoch)), MessageValidity.Ignored, typeof(SignedExecutionPayloadEnvelope), null);
        yield return Case("envelope withdrawal count follows block checks, so only drops", Topic(GloasDigest, GossipTopics.ExecutionPayload),
            Encode(Envelope(withdrawals: Presets.MaxWithdrawalsPerPayload + 1)), MessageValidity.Ignored, null, GossipDropReason.LimitExceeded);
    }

    [TestCaseSource(nameof(Cases))]
    public void Verdict_and_consumption_follow_the_gossip_rules(string topic, byte[] data, bool signed, MessageValidity expected, Type? consumedAs, GossipDropReason? reason)
    {
        (GossipMessageValidator validator, GossipRouter router, List<object> raised) = Create();

        MessageValidity validity = validator.Verify(Message(topic, data, signed));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(validity, Is.EqualTo(expected), "validity");
            Assert.That(raised.Select(static e => e.GetType()), consumedAs is null ? Is.Empty : Is.EqualTo(new[] { consumedAs }), "typed event");
            Assert.That(Enum.GetValues<GossipDropReason>().Select(router.GetDropCount).Sum(), Is.EqualTo(reason is null ? 0 : 1), "one drop at most");
            if (reason is { } expectedReason)
            {
                Assert.That(router.GetDropCount(expectedReason), Is.EqualTo(1), "drop reason");
            }
        }
    }

    // altair p2p "Transitioning the gossip": a digest is handled from one epoch before it takes effect until two epochs after its last epoch.
    [TestCase(-1, true, MessageValidity.Rejected, GossipDropReason.InvalidSnappy, TestName = "next-fork digest one epoch before it takes effect")]
    [TestCase(-2, true, MessageValidity.Ignored, GossipDropReason.UnknownTopic, TestName = "next-fork digest two epochs before it takes effect")]
    [TestCase(1, false, MessageValidity.Rejected, GossipDropReason.InvalidSnappy, TestName = "previous-fork digest two epochs after its last epoch")]
    [TestCase(2, false, MessageValidity.Ignored, GossipDropReason.UnknownTopic, TestName = "previous-fork digest three epochs after its last epoch")]
    public void Digest_is_handled_only_inside_the_fork_transition_window(int wallEpochFromGloas, bool gloasDigest, MessageValidity expected, GossipDropReason reason)
    {
        ulong wallEpoch = (ulong)((long)Sepolia.GloasForkEpoch + wallEpochFromGloas);
        (GossipMessageValidator validator, GossipRouter router, List<object> _) = Create(new ManualTimestamper(SlotStart(wallEpoch * Sepolia.SlotsPerEpoch).AddSeconds(6)));

        MessageValidity validity = validator.Verify(Message(Topic(gloasDigest ? GloasDigest : FuluDigest, GossipTopics.BeaconBlock), [0xff, 0xff, 0xff, 0xff], signed: false));

        Assert.That((validity, router.GetDropCount(reason)), Is.EqualTo((expected, 1L)), "an invalid snappy payload is Rejected only on a handled digest");
    }

    // deneb p2p is_current_or_previous_epoch: an aggregate two epochs old stays in range until the disparity past the epoch start.
    [TestCase(0, true)]
    [TestCase(MaximumGossipClockDisparityMs, true)]
    [TestCase(MaximumGossipClockDisparityMs + 1, false)]
    public void Aggregate_two_epochs_old_is_handled_within_the_clock_disparity_of_the_epoch_start(long msIntoEpoch, bool consumed)
    {
        ulong wallEpoch = Sepolia.GloasForkEpoch + 2;
        (GossipMessageValidator validator, GossipRouter router, List<object> raised) = Create(new ManualTimestamper(SlotStart(wallEpoch * Sepolia.SlotsPerEpoch).AddMilliseconds(msIntoEpoch)));

        validator.Verify(Message(Topic(GloasDigest, GossipTopics.BeaconAggregateAndProof), Encode(GloasAggregate(slot: FirstGloasSlot + 3)), signed: false));

        Assert.That((raised.Count, router.GetDropCount(GossipDropReason.StaleSlot)), Is.EqualTo(consumed ? (1, 0L) : (0, 1L)));
    }

    [Test]
    public void Block_is_ignored_once_its_slot_and_proposer_have_a_block_with_a_valid_signature()
    {
        (GossipMessageValidator validator, GossipRouter router, List<object> raised) = Create();
        router.MarkProposalSeen(WallSlot, GloasBlock().Message!.ProposerIndex);

        MessageValidity validity = validator.Verify(Message(Topic(GloasDigest, GossipTopics.BeaconBlock), Encode(GloasBlock()), signed: false));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(validity, Is.EqualTo(MessageValidity.Ignored));
            Assert.That(raised, Is.Empty, "the block is dropped, not consumed");
            Assert.That(router.GetDropCount(GossipDropReason.Duplicate), Is.EqualTo(1));
        }
    }

    [Test]
    public void Early_next_slot_block_is_held_and_consumed_once_its_slot_starts()
    {
        ManualTimestamper timestamper = new(SlotStart(WallSlot).AddSeconds(6));
        (GossipMessageValidator validator, GossipRouter _, List<object> raised) = Create(timestamper);

        MessageValidity early = validator.Verify(Message(Topic(GloasDigest, GossipTopics.BeaconBlock), Encode(GloasBlock(WallSlot + 1)), signed: false));
        Assert.That((early, raised.Count), Is.EqualTo((MessageValidity.Ignored, 0)), "held, not raised, while its slot is more than the clock disparity away");

        timestamper.Set(SlotStart(WallSlot + 1));
        validator.Verify(Message(Topic(GloasDigest, GossipTopics.BeaconAggregateAndProof), Encode(GloasAggregate(slot: WallSlot + 1)), signed: false));

        Assert.That(raised.Select(static e => e.GetType()), Is.EqualTo(new[] { typeof(ForkedSignedBeaconBlock.OfGloas), typeof(SignedAggregateAndProofGloas) }),
            "the held block is raised when the next message arrives in its slot, before that message");
    }

    private static (GossipMessageValidator Validator, GossipRouter Router, List<object> Raised) Create(ManualTimestamper? timestamper = null)
    {
        timestamper ??= new ManualTimestamper(SlotStart(WallSlot).AddSeconds(6));
        SlotClock clock = new(Sepolia, timestamper);
        BeaconChainStatusHolder status = new(Sepolia, timestamper)
        {
            CurrentStatus = new StatusMessageV2 { ForkDigest = GloasDigest, FinalizedRoot = Hash256.Zero, FinalizedEpoch = FinalizedEpoch, HeadRoot = Hash256.Zero },
        };
        GossipRouter router = new(Sepolia, clock, LimboLogs.Instance, status: status);
        List<object> raised = [];
        router.BeaconBlockReceived += raised.Add;
        router.AggregateAndProofReceived += raised.Add;
        router.GloasAggregateAndProofReceived += raised.Add;
        router.AttesterSlashingReceived += raised.Add;
        router.GloasAttesterSlashingReceived += raised.Add;
        router.ExecutionPayloadEnvelopeReceived += raised.Add;
        return (new GossipMessageValidator(router, new ColumnGossipRouter(Sepolia, clock, LimboLogs.Instance), Sepolia, clock), router, raised);
    }

    private static DateTime SlotStart(ulong slot) => DateTime.UnixEpoch.AddSeconds(Sepolia.GenesisTime + slot * Sepolia.SecondsPerSlot);

    private static TestCaseData Case(string name, string topic, byte[] data, MessageValidity expected, Type? consumedAs, GossipDropReason? reason, bool signed = false) =>
        new TestCaseData(topic, data, signed, expected, consumedAs, reason).SetName(name);

    private static Message Message(string topic, byte[] data, bool signed) => new()
    {
        Topic = topic,
        Data = ByteString.CopyFrom(data),
        Signature = signed ? ByteString.CopyFrom([1]) : ByteString.Empty,
    };

    private static string Topic(byte[] digest, string name) => GossipTopics.Topic(digest, name);

    private static byte[] Compress(byte[] ssz) => Snappy.CompressToArray(ssz);

    private static byte[] Encode(SignedBeaconBlockGloas block) => Compress(SignedBeaconBlockGloas.Encode(block));

    internal static byte[] Encode(SignedAggregateAndProofGloas aggregate) => Compress(SignedAggregateAndProofGloas.Encode(aggregate));

    private static byte[] Encode(SignedAggregateAndProof aggregate) => Compress(SignedAggregateAndProof.Encode(aggregate));

    internal static byte[] Encode(AttesterSlashingGloas slashing) => Compress(AttesterSlashingGloas.Encode(slashing));

    private static byte[] Encode(AttesterSlashing slashing) => Compress(AttesterSlashing.Encode(slashing));

    private static byte[] Encode(SignedBeaconBlock block) => Compress(SignedBeaconBlock.Encode(block));

    private static byte[] Encode(SignedExecutionPayloadEnvelope envelope) => Compress(SignedExecutionPayloadEnvelope.Encode(envelope));

    private static SignedBeaconBlockGloas GloasBlock(ulong? slot = null, Action<SignedBeaconBlockGloas>? mutate = null)
    {
        SignedBeaconBlockGloas block = CreateMinimalGloasBlock(slot ?? WallSlot);
        mutate?.Invoke(block);
        return block;
    }

    private static SignedBeaconBlock FuluBlock(int blobs)
    {
        SignedBeaconBlock block = CreateMinimalBlock(FuluSlot);
        block.Message!.Body!.BlobKzgCommitments = Commitments(blobs);
        return block;
    }

    private static SignedVoluntaryExit Exit(ulong validatorIndex) => new() { Message = new VoluntaryExit { Epoch = 1, ValidatorIndex = validatorIndex } };

    private static ExecutionRequestsGloas RequestsOverLimit() => new()
    {
        BuilderExits = [.. Enumerable.Range(0, Presets.MaxBuilderExitRequestsPerPayload + 1).Select(static _ => new BuilderExitRequest { SourceAddress = Address.Zero, Pubkey = new BlsPublicKey(new byte[BlsPublicKey.Length]) })],
    };

    private static SszKzgCommitment[] Commitments(int count) => [.. Enumerable.Range(0, count).Select(static _ => SszKzgCommitment.FromSpan(new byte[SszKzgCommitment.KzgCommitmentLength]))];

    private static AttestationData AttestationData(ulong slot, ulong index, ulong? targetEpoch = null) => new()
    {
        Slot = slot,
        Index = index,
        BeaconBlockRoot = Hash256.Zero,
        Source = new Checkpoint { Epoch = 1, Root = Hash256.Zero },
        Target = new Checkpoint { Epoch = targetEpoch ?? Sepolia.GetEpoch(slot), Root = Hash256.Zero },
    };

    private static BitArray Bits(int length, int set)
    {
        BitArray bits = new(length);
        for (int i = 0; i < set; i++)
        {
            bits[i] = true;
        }

        return bits;
    }

    internal static SignedAggregateAndProofGloas GloasAggregate(ulong? slot = null, ulong index = 0, int committees = 1, int participants = 1, ulong? targetEpoch = null) => new()
    {
        Message = new AggregateAndProofGloas
        {
            AggregatorIndex = 7,
            Aggregate = new AttestationGloas
            {
                AggregationBits = Bits(8, participants),
                Data = AttestationData(slot ?? WallSlot, index, targetEpoch),
                CommitteeBits = Bits(64, committees),
            },
        },
    };

    private static SignedAggregateAndProof FuluAggregate(ulong index) => new()
    {
        Message = new AggregateAndProof
        {
            AggregatorIndex = 7,
            Aggregate = new Attestation
            {
                AggregationBits = Bits(8, 1),
                Data = AttestationData(FuluSlot, index),
                CommitteeBits = Bits(64, 1),
            },
        },
    };

    // The first vote spans source 1 to target 4, so a second vote from 2 to 3 is surrounded by it.
    internal static AttesterSlashingGloas GloasSlashing(ulong[] indices1, ulong[] indices2, ulong secondSource, ulong secondTarget) => new()
    {
        Attestation1 = new IndexedAttestationGloas { AttestingIndices = indices1, Data = Vote(1, 4) },
        Attestation2 = new IndexedAttestationGloas { AttestingIndices = indices2, Data = Vote(secondSource, secondTarget) },
    };

    private static AttesterSlashing FuluSlashing(ulong[] indices1, ulong[] indices2, ulong secondSource, ulong secondTarget) => new()
    {
        Attestation1 = new IndexedAttestation { AttestingIndices = indices1, Data = Vote(1, 4) },
        Attestation2 = new IndexedAttestation { AttestingIndices = indices2, Data = Vote(secondSource, secondTarget) },
    };

    private static AttestationData Vote(ulong source, ulong target) => new()
    {
        Slot = WallSlot,
        Index = 0,
        BeaconBlockRoot = Hash256.Zero,
        Source = new Checkpoint { Epoch = source, Root = Hash256.Zero },
        Target = new Checkpoint { Epoch = target, Root = Hash256.Zero },
    };

    private static SignedExecutionPayloadEnvelope Envelope(ulong? slot = null, ExecutionRequestsGloas? requests = null, int withdrawals = 0) => new()
    {
        Message = new ExecutionPayloadEnvelope
        {
            Payload = new ExecutionPayloadGloas
            {
                SlotNumber = slot ?? WallSlot,
                Withdrawals = [.. Enumerable.Range(0, withdrawals).Select(static i => new Nethermind.BeaconChain.Types.Withdrawal { Index = (ulong)i, Address = Address.Zero })],
            },
            ExecutionRequests = requests ?? new ExecutionRequestsGloas(),
            BeaconBlockRoot = Hash256.Zero,
            ParentBeaconBlockRoot = Hash256.Zero,
        },
    };
}
