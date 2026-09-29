// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections;
using System.Linq;
using System.Security.Cryptography;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.StateTransition.Shuffling;
using Nethermind.BeaconChain.Test.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.StateTransition.GloasTestFixtures;
using FuluStateTransition = Nethermind.BeaconChain.StateTransition.StateTransition;

namespace Nethermind.BeaconChain.Test.StateTransition;

/// <summary>
/// <c>process_block</c> with its signatures batched against the same block run serially: the same
/// blocks are accepted with the same post-state, and the same blocks are refused with the message the
/// serial path gives, whichever signature is bad and whatever else in the block fails.
/// </summary>
[TestFixture]
public class BlockSignatureBatchProcessingTests
{
    /// <summary>Every block-validity signature a Gloas block carries, in <c>process_block</c> order.</summary>
    public enum SignedPart
    {
        Bid,
        Randao,
        ProposerSlashing1,
        ProposerSlashing2,
        AttesterSlashing1,
        AttesterSlashing2,
        Attestation,
        BlsChange,
        PayloadAttestation,
        SyncAggregate,
    }

    private const int Equivocator = 2000;
    private const int BlsChanger = 7;
    private const int SyncParticipants = 16;
    private const ulong BlockSlot = 33;
    private const ulong ParentSlot = 32;

    // Decodes and lies in G2, so the batch defers it and only the pairing refuses it.
    private static readonly BlsSignature WrongSignature = Sign(DeriveKey(999), Hash(0xEE));

    // The key CreateGloasState gives builder 0, and the key validator BlsChanger's BLS credentials commit to.
    private const int BuilderKeyIndex = 200;
    private const int BlsChangeKeyIndex = 400;

    private sealed record GloasFixture(BeaconStateGloas BeforeSlots, BeaconStateGloas Pre, PubkeyCache Pubkeys);

    private static readonly Lazy<GloasFixture> SharedGloas = new(CreateGloasFixture);

    private static GloasFixture CreateGloasFixture()
    {
        BeaconStateGloas state = CreateGloasState(out _, out _);
        PubkeyCache pubkeys = InstallRealValidatorKeys(state);
        Validator changer = state.Validators![BlsChanger].Clone();
        changer.WithdrawalCredentials = BlsWithdrawalCredentials(new BlsPublicKey(new Bls.P1(DeriveKey(BlsChangeKeyIndex)).Compress()));
        state.Validators[BlsChanger] = changer;

        EpochCache cache = new();
        ApplyBlock(state, MinimalBlock(state, SelfBuildBid(state, state.LatestBlockHash!, Hash(0x99))), cache);
        BeaconStateGloas beforeSlots = state.Clone();
        GloasSlotProcessing.ProcessSlots(state, BlockSlot, cache);
        return new GloasFixture(beforeSlots, state, pubkeys);
    }

    /// <summary>A block at <see cref="BlockSlot"/> carrying one of every signature kind, each validly signed.</summary>
    private static BeaconBlockGloas SignedGloasBlock()
    {
        GloasFixture fixture = SharedGloas.Value;
        BeaconStateGloas pre = fixture.Pre;
        BeaconBlockGloas block = MinimalBlock(pre, ValidBuilderBid(pre, DeriveKey(BuilderKeyIndex), builderIndex: 0, value: Gwei)).Message!;
        BeaconBlockBodyGloas body = block.Body!;
        ulong epoch = pre.GetCurrentEpoch();
        Hash256 parentRoot = pre.GetBlockRootAtSlot(ParentSlot);

        body.RandaoReveal = Sign(ValidatorKey((int)block.ProposerIndex), Domains.ComputeSigningRoot(EpochRoot(epoch), pre.GetDomain(DomainType.Randao, epoch)));
        body.ProposerSlashings = [Equivocation(pre, slot: ParentSlot, Equivocator)];
        body.AttesterSlashings =
        [
            new AttesterSlashingGloas
            {
                Attestation1 = SignedIndexedAttestation(pre, Vote(slot: ParentSlot, sourceEpoch: 0, targetEpoch: epoch, fill: 0xA0), [1, 2, 3]),
                Attestation2 = SignedIndexedAttestation(pre, Vote(slot: ParentSlot, sourceEpoch: 0, targetEpoch: epoch, fill: 0xB0), [2, 3, 4]),
            },
        ];
        CommitteeCache committees = new EpochCache().GetCommitteeCache(pre, epoch);
        body.Attestations = [CommitteeAttestation(pre, VoteFor(pre, ParentSlot, epoch, parentRoot), committees, committeeIndex: 0, sign: true)];
        body.BlsToExecutionChanges = [SignedBlsChange(pre, BlsChanger, DeriveKey(BlsChangeKeyIndex), new Address(Hash(0xCC).Bytes[12..]))];
        PayloadAttestationData ptcData = new() { BeaconBlockRoot = parentRoot, Slot = ParentSlot, PayloadPresent = false, BlobDataAvailable = false };
        body.PayloadAttestations = [PtcAttestation(pre, ptcData, [0, 1, 2], sign: true)];

        BitArray bits = new(Presets.SyncCommitteeSize);
        for (int i = 0; i < SyncParticipants; i++)
        {
            bits[i] = true;
        }
        Hash256 syncRoot = Domains.ComputeSigningRoot(parentRoot, pre.GetDomain(DomainType.SyncCommittee, BeaconStateAccessors.ComputeEpochAtSlot(ParentSlot)));
        // InstallRealValidatorKeys seats validator 0 in every sync committee position.
        body.SyncAggregate = new SyncAggregate { SyncCommitteeBits = bits, SyncCommitteeSignature = AggregateSignature(syncRoot, new int[SyncParticipants]) };
        return block;
    }

    private static void Replace(BeaconBlockGloas block, SignedPart part, BlsSignature signature)
    {
        BeaconBlockBodyGloas body = block.Body!;
        switch (part)
        {
            case SignedPart.Bid: body.SignedExecutionPayloadBid!.Signature = signature; break;
            case SignedPart.Randao: body.RandaoReveal = signature; break;
            case SignedPart.ProposerSlashing1: body.ProposerSlashings![0].SignedHeader1!.Signature = signature; break;
            case SignedPart.ProposerSlashing2: body.ProposerSlashings![0].SignedHeader2!.Signature = signature; break;
            case SignedPart.AttesterSlashing1: body.AttesterSlashings![0].Attestation1!.Signature = signature; break;
            case SignedPart.AttesterSlashing2: body.AttesterSlashings![0].Attestation2!.Signature = signature; break;
            case SignedPart.Attestation: body.Attestations![0].Signature = signature; break;
            case SignedPart.BlsChange: body.BlsToExecutionChanges![0].Signature = signature; break;
            case SignedPart.PayloadAttestation: body.PayloadAttestations![0].Signature = signature; break;
            case SignedPart.SyncAggregate: body.SyncAggregate!.SyncCommitteeSignature = signature; break;
            default: throw new ArgumentOutOfRangeException(nameof(part), part, null);
        }
    }

    private static string SerialRefusal(SignedPart part) => part switch
    {
        SignedPart.Bid => "Invalid execution payload bid signature",
        SignedPart.Randao => "Invalid RANDAO reveal",
        SignedPart.ProposerSlashing1 => "Invalid proposer slashing signature 1",
        SignedPart.ProposerSlashing2 => "Invalid proposer slashing signature 2",
        SignedPart.AttesterSlashing1 => "Attester slashing attestation 1 is invalid",
        SignedPart.AttesterSlashing2 => "Attester slashing attestation 2 is invalid",
        SignedPart.Attestation => "Invalid indexed attestation",
        SignedPart.BlsChange => "Invalid BLS to execution change signature",
        SignedPart.PayloadAttestation => "Invalid indexed payload attestation",
        SignedPart.SyncAggregate => "Invalid sync aggregate signature",
        _ => throw new ArgumentOutOfRangeException(nameof(part), part, null),
    };

    private readonly record struct Outcome(string? Refusal, Hash256? Root)
    {
        public override string ToString() => Refusal ?? $"accepted with root {Root}";
    }

    private static Outcome RunGloas(BeaconBlockGloas block, bool batched)
    {
        GloasFixture fixture = SharedGloas.Value;
        BeaconStateGloas state = fixture.Pre.Clone();
        try
        {
            if (batched)
                GloasBlockProcessing.ProcessBlock(state, block, new EpochCache(), fixture.Pubkeys, new AcceptingNotifier(), UpgradeEpochSpec());
            else
                GloasBlockProcessing.ProcessBlock(state, block, new EpochCache(), fixture.Pubkeys, new AcceptingNotifier(), UpgradeEpochSpec(), verifySignatures: true, batch: null);
        }
        catch (BeaconStateException e)
        {
            return new Outcome(e.Message, null);
        }

        return new Outcome(null, SszRoots.HashTreeRoot(state));
    }

    private static void AssertBothRefuse(Outcome serial, Outcome batched, string refusal)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(serial.Refusal, Is.EqualTo(refusal), "serial");
            Assert.That(batched, Is.EqualTo(serial), "batched against serial");
        }
    }

    [Test]
    public void A_fully_signed_gloas_block_is_accepted_with_the_serial_post_state_after_deferring_every_signature()
    {
        BeaconBlockGloas block = SignedGloasBlock();
        GloasFixture fixture = SharedGloas.Value;
        BlockSignatureBatch batch = new();
        GloasBlockProcessing.ProcessBlock(fixture.Pre.Clone(), block, new EpochCache(), fixture.Pubkeys, new AcceptingNotifier(), UpgradeEpochSpec(), verifySignatures: true, batch);

        Outcome serial = RunGloas(block, batched: false);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(batch.Count, Is.EqualTo(Enum.GetValues<SignedPart>().Length), "every signature is deferred, none verified on the spot");
            Assert.That(serial.Refusal, Is.Null, "fixture bug: the serial path must accept the block");
            Assert.That(RunGloas(block, batched: true), Is.EqualTo(serial));
        }
    }

    [Test]
    public void One_bad_signature_anywhere_in_a_gloas_block_is_refused_with_the_serial_message([Values] SignedPart part)
    {
        BeaconBlockGloas block = SignedGloasBlock();
        Replace(block, part, WrongSignature);

        AssertBothRefuse(RunGloas(block, batched: false), RunGloas(block, batched: true), SerialRefusal(part));
    }

    /// <summary>
    /// Serial verification stops at the bad signature, so a later failing operation is never reached; the
    /// batch reaches it first and must still report the signature.
    /// </summary>
    [Test]
    public void A_bad_signature_ahead_of_a_failing_operation_is_the_reported_refusal(
        [Values(SignedPart.Bid, SignedPart.Randao, SignedPart.ProposerSlashing1, SignedPart.ProposerSlashing2, SignedPart.AttesterSlashing1,
            SignedPart.AttesterSlashing2, SignedPart.Attestation, SignedPart.BlsChange)] SignedPart part)
    {
        BeaconBlockGloas block = SignedGloasBlock();
        Replace(block, part, WrongSignature);
        block.Body!.PayloadAttestations![0].Data!.Slot = ParentSlot - 1;

        AssertBothRefuse(RunGloas(block, batched: false), RunGloas(block, batched: true), SerialRefusal(part));
    }

    [Test]
    public void A_failing_operation_ahead_of_a_bad_signature_is_the_reported_refusal()
    {
        BeaconBlockGloas block = SignedGloasBlock();
        block.Body!.Attestations![0].Data!.Index = 2;
        Replace(block, SignedPart.SyncAggregate, WrongSignature);

        AssertBothRefuse(RunGloas(block, batched: false), RunGloas(block, batched: true), "Attestation data index 2 must encode a payload status (0 or 1)");
    }

    /// <summary>
    /// The import path catches only <see cref="BeaconStateException"/>, so any other failure after a deferred bad
    /// signature must still surface as that signature's refusal, as the serial path stops there.
    /// </summary>
    [Test]
    public void A_bad_signature_ahead_of_an_unexpected_exception_is_the_reported_refusal()
    {
        BeaconBlockGloas block = SignedGloasBlock();
        Replace(block, SignedPart.Randao, WrongSignature);
        block.Body!.Attestations![0].Data = null;

        AssertBothRefuse(RunGloas(block, batched: false), RunGloas(block, batched: true), SerialRefusal(SignedPart.Randao));
    }

    [Test]
    public void Without_signature_verification_nothing_is_deferred_or_refused()
    {
        BeaconBlockGloas block = SignedGloasBlock();
        foreach (SignedPart part in Enum.GetValues<SignedPart>())
        {
            Replace(block, part, WrongSignature);
        }
        GloasFixture fixture = SharedGloas.Value;
        BlockSignatureBatch batch = new();

        GloasBlockProcessing.ProcessBlock(fixture.Pre.Clone(), block, new EpochCache(), fixture.Pubkeys, new AcceptingNotifier(), UpgradeEpochSpec(), verifySignatures: false, batch);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(batch.Count, Is.Zero);
            Assert.That(() => GloasBlockProcessing.ProcessBlock(fixture.Pre.Clone(), block, new EpochCache(), fixture.Pubkeys, new AcceptingNotifier(), UpgradeEpochSpec(), verifySignatures: false), Throws.Nothing);
        }
    }

    // ---- Fulu ----

    private const ulong FuluBlockSlot = 1;

    /// <summary>The block-validity signatures a Fulu block carries, in <c>process_block</c> order.</summary>
    private static readonly SignedPart[] FuluParts =
    [
        SignedPart.Randao, SignedPart.ProposerSlashing1, SignedPart.ProposerSlashing2, SignedPart.AttesterSlashing1,
        SignedPart.AttesterSlashing2, SignedPart.Attestation, SignedPart.BlsChange, SignedPart.SyncAggregate,
    ];

    private sealed record FuluFixture(SignedGloasChain Chain, BeaconStateFulu Anchor, PubkeyCache Pubkeys);

    private static readonly Lazy<FuluFixture> SharedFulu = new(static () =>
    {
        SignedGloasChain chain = new();
        BeaconStateFulu anchor = chain.AnchorState.Clone();
        Validator[] validators = [.. anchor.Validators!];
        Validator changer = validators[BlsChanger].Clone();
        changer.WithdrawalCredentials = BlsWithdrawalCredentials(new BlsPublicKey(new Bls.P1(DeriveKey(BlsChangeKeyIndex)).Compress()));
        validators[BlsChanger] = changer;
        anchor.Validators = validators;

        PubkeyCache pubkeys = new();
        pubkeys.Build(anchor.Validators);
        return new FuluFixture(chain, anchor, pubkeys);
    });

    /// <summary>A block at <see cref="FuluBlockSlot"/> carrying one of every signature kind in <see cref="FuluParts"/>, each validly signed.</summary>
    private static BeaconBlock SignedFuluBlock()
    {
        BeaconBlock block = SharedFulu.Value.Chain.NextFulu(FuluBlockSlot).Signed.Message!;
        BeaconStateFulu pre = SharedFulu.Value.Anchor.Clone();
        SlotProcessing.ProcessSlots(pre, FuluBlockSlot, new EpochCache());
        // The rewritten credentials change the anchor's state root, which slot processing seals into the parent header.
        block.ParentRoot = SszRoots.HashTreeRoot(pre.LatestBlockHeader!);
        BeaconBlockBody body = block.Body!;
        const ulong epoch = 0;
        const ulong voteSlot = FuluBlockSlot - 1;

        body.ProposerSlashings = [new ProposerSlashing { SignedHeader1 = SignedFuluHeader(pre, voteSlot, Hash(0x21)), SignedHeader2 = SignedFuluHeader(pre, voteSlot, Hash(0x22)) }];
        body.AttesterSlashings =
        [
            new AttesterSlashing
            {
                Attestation1 = SignedFuluIndexedAttestation(pre, Vote(slot: voteSlot, sourceEpoch: 0, targetEpoch: epoch, fill: 0xA0), [1, 2, 3]),
                Attestation2 = SignedFuluIndexedAttestation(pre, Vote(slot: voteSlot, sourceEpoch: 0, targetEpoch: epoch, fill: 0xB0), [2, 3, 4]),
            },
        ];

        AttestationData vote = new()
        {
            Slot = voteSlot,
            Index = 0,
            BeaconBlockRoot = pre.GetBlockRootAtSlot(voteSlot),
            Source = pre.CurrentJustifiedCheckpoint,
            Target = new Checkpoint { Epoch = epoch, Root = pre.GetBlockRoot(epoch) },
        };
        int[] committee = new EpochCache().GetCommitteeCache(pre, epoch).GetBeaconCommittee(voteSlot, 0).ToArray();
        BitArray committeeBits = new(Presets.MaxCommitteesPerSlot) { [0] = true };
        Hash256 attesterRoot = Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(vote), pre.GetDomain(DomainType.BeaconAttester, epoch));
        body.Attestations = [new Attestation { AggregationBits = new BitArray(committee.Length, true), CommitteeBits = committeeBits, Data = vote, Signature = AggregateSignature(attesterRoot, committee) }];

        BlsToExecutionChange change = new()
        {
            ValidatorIndex = BlsChanger,
            FromBlsPubkey = new BlsPublicKey(new Bls.P1(DeriveKey(BlsChangeKeyIndex)).Compress()),
            ToExecutionAddress = new Address(Hash(0xCC).Bytes[12..]),
        };
        Hash256 changeDomain = Domains.ComputeDomain(DomainType.BlsToExecutionChange, BeaconChainSpec.ForGenesisValidatorsRoot(pre.GenesisValidatorsRoot!).GenesisForkVersion, pre.GenesisValidatorsRoot!);
        body.BlsToExecutionChanges = [new SignedBlsToExecutionChange { Message = change, Signature = Sign(DeriveKey(BlsChangeKeyIndex), Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(change), changeDomain)) }];

        BitArray bits = new(Presets.SyncCommitteeSize);
        for (int i = 0; i < SyncParticipants; i++)
        {
            bits[i] = true;
        }
        Hash256 syncRoot = Domains.ComputeSigningRoot(pre.GetBlockRootAtSlot(voteSlot), pre.GetDomain(DomainType.SyncCommittee, epoch));
        // The anchor seats validator 0 in every sync committee position.
        body.SyncAggregate = new SyncAggregate { SyncCommitteeBits = bits, SyncCommitteeSignature = AggregateSignature(syncRoot, new int[SyncParticipants]) };
        return block;
    }

    private static SignedBeaconBlockHeader SignedFuluHeader(BeaconStateFulu state, ulong slot, Hash256 bodyRoot)
    {
        BeaconBlockHeader header = new() { Slot = slot, ProposerIndex = Equivocator, ParentRoot = Hash(0x11), StateRoot = Hash(0x12), BodyRoot = bodyRoot };
        Hash256 domain = state.GetDomain(DomainType.BeaconProposer, BeaconStateAccessors.ComputeEpochAtSlot(slot));
        return new SignedBeaconBlockHeader { Message = header, Signature = Sign(ValidatorKey(Equivocator), Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(header), domain)) };
    }

    private static IndexedAttestation SignedFuluIndexedAttestation(BeaconStateFulu state, AttestationData data, int[] validatorIndices)
    {
        Hash256 domain = state.GetDomain(DomainType.BeaconAttester, data.Target!.Epoch);
        return new IndexedAttestation
        {
            AttestingIndices = [.. validatorIndices.Select(static i => (ulong)i)],
            Data = data,
            Signature = AggregateSignature(Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(data), domain), validatorIndices),
        };
    }

    private static void Replace(BeaconBlock block, SignedPart part, BlsSignature signature)
    {
        BeaconBlockBody body = block.Body!;
        switch (part)
        {
            case SignedPart.Randao: body.RandaoReveal = signature; break;
            case SignedPart.ProposerSlashing1: body.ProposerSlashings![0].SignedHeader1!.Signature = signature; break;
            case SignedPart.ProposerSlashing2: body.ProposerSlashings![0].SignedHeader2!.Signature = signature; break;
            case SignedPart.AttesterSlashing1: body.AttesterSlashings![0].Attestation1!.Signature = signature; break;
            case SignedPart.AttesterSlashing2: body.AttesterSlashings![0].Attestation2!.Signature = signature; break;
            case SignedPart.Attestation: body.Attestations![0].Signature = signature; break;
            case SignedPart.BlsChange: body.BlsToExecutionChanges![0].Signature = signature; break;
            case SignedPart.SyncAggregate: body.SyncAggregate!.SyncCommitteeSignature = signature; break;
            default: throw new ArgumentOutOfRangeException(nameof(part), part, "a Fulu block has no such signature");
        }
    }

    private static Outcome RunFulu(BeaconBlock block, bool batched, BlockSignatureBatch? batch = null)
    {
        FuluFixture fixture = SharedFulu.Value;
        BeaconStateFulu state = fixture.Anchor.Clone();
        EpochCache cache = new();
        SlotProcessing.ProcessSlots(state, block.Slot, cache);
        ulong maxBlobs = fixture.Chain.Spec.MaxBlobsPerBlockElectra;
        try
        {
            if (batched)
                BlockProcessing.ProcessBlock(state, block, cache, fixture.Pubkeys, new AcceptingNotifier(), maxBlobs);
            else
                BlockProcessing.ProcessBlock(state, block, cache, fixture.Pubkeys, new AcceptingNotifier(), maxBlobs, verifySignatures: true, batch);
        }
        catch (BeaconStateException e)
        {
            return new Outcome(e.Message, null);
        }

        return new Outcome(null, SszRoots.HashTreeRoot(state));
    }

    /// <summary>An aggregate the Fulu attestation rules refuse before its signature is looked at.</summary>
    private static Attestation StructurallyInvalidAttestation() => new()
    {
        AggregationBits = new BitArray(1, true),
        CommitteeBits = new BitArray(Presets.MaxCommitteesPerSlot),
        Data = new AttestationData
        {
            Slot = FuluBlockSlot - 1,
            Index = 1,
            BeaconBlockRoot = Hash256.Zero,
            Source = new Checkpoint { Epoch = 0, Root = Hash256.Zero },
            Target = new Checkpoint { Epoch = 0, Root = Hash256.Zero },
        },
        Signature = WrongSignature,
    };

    [Test]
    public void A_fully_signed_fulu_block_is_accepted_with_the_serial_post_state_after_deferring_every_signature()
    {
        BeaconBlock block = SignedFuluBlock();
        BlockSignatureBatch batch = new();
        Outcome serial = RunFulu(block, batched: false);
        RunFulu(block, batched: false, batch);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(serial.Refusal, Is.Null, "fixture bug: the serial path must accept the block");
            Assert.That(batch.Count, Is.EqualTo(FuluParts.Length), "every signature is deferred, none verified on the spot");
            Assert.That(RunFulu(block, batched: true), Is.EqualTo(serial));
        }
    }

    [Test]
    public void One_bad_signature_anywhere_in_a_fulu_block_is_refused_with_the_serial_message([ValueSource(nameof(FuluParts))] SignedPart part)
    {
        BeaconBlock block = SignedFuluBlock();
        Replace(block, part, WrongSignature);

        AssertBothRefuse(RunFulu(block, batched: false), RunFulu(block, batched: true), SerialRefusal(part));
    }

    [Test]
    public void A_bad_fulu_signature_ahead_of_a_failing_operation_is_the_reported_refusal(
        [Values(SignedPart.Randao, SignedPart.ProposerSlashing1, SignedPart.ProposerSlashing2, SignedPart.AttesterSlashing1,
            SignedPart.AttesterSlashing2, SignedPart.Attestation)] SignedPart part)
    {
        BeaconBlock block = SignedFuluBlock();
        Replace(block, part, WrongSignature);
        block.Body!.BlsToExecutionChanges![0].Message!.ValidatorIndex = ulong.MaxValue;

        AssertBothRefuse(RunFulu(block, batched: false), RunFulu(block, batched: true), SerialRefusal(part));
    }

    [Test]
    public void A_failing_fulu_operation_ahead_of_a_bad_signature_is_the_reported_refusal()
    {
        BeaconBlock block = SignedFuluBlock();
        block.Body!.Attestations = [StructurallyInvalidAttestation()];
        Replace(block, SignedPart.SyncAggregate, WrongSignature);

        AssertBothRefuse(RunFulu(block, batched: false), RunFulu(block, batched: true), "Attestation data index 1 must be zero");
    }

    /// <summary>
    /// A voluntary exit needs <c>SHARD_COMMITTEE_PERIOD</c> epochs of activity, which no block of these fixtures
    /// reaches, so its deferral is checked on the operation against a state moved past that period.
    /// </summary>
    [Test]
    public void A_voluntary_exit_signature_is_deferred_and_refused_with_the_serial_message([Values] bool gloas, [Values] bool validSignature)
    {
        const string refusal = "Invalid voluntary exit signature";
        const int exiter = 9;
        ulong exitSlot = (Presets.ShardCommitteePeriod + 1) * Presets.SlotsPerEpoch;
        Action<BlockSignatureBatch?> process;
        if (gloas)
        {
            BeaconStateGloas state = SharedGloas.Value.Pre.Clone();
            state.Slot = exitSlot;
            SignedVoluntaryExit exit = SignedExit(state, exiter, epoch: Presets.ShardCommitteePeriod);
            if (!validSignature)
                exit.Signature = WrongSignature;
            PubkeyCache pubkeys = SharedGloas.Value.Pubkeys;
            process = batch => GloasBlockProcessing.ProcessVoluntaryExit(state.Clone(), exit, new EpochCache(), pubkeys, verifySignature: true, batch);
        }
        else
        {
            BeaconStateFulu state = SharedFulu.Value.Anchor.Clone();
            state.Slot = exitSlot;
            VoluntaryExit message = new() { Epoch = Presets.ShardCommitteePeriod, ValidatorIndex = exiter };
            Hash256 domain = Domains.ComputeDomain(DomainType.VoluntaryExit, BeaconChainSpec.ForGenesisValidatorsRoot(state.GenesisValidatorsRoot!).CapellaForkVersion, state.GenesisValidatorsRoot!);
            SignedVoluntaryExit exit = new()
            {
                Message = message,
                Signature = validSignature ? Sign(ValidatorKey(exiter), Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(message), domain)) : WrongSignature,
            };
            PubkeyCache pubkeys = SharedFulu.Value.Pubkeys;
            process = batch => BlockProcessing.ProcessVoluntaryExit(state.Clone(), exit, new EpochCache(), pubkeys, verifySignature: true, batch);
        }

        BlockSignatureBatch deferred = new();
        process(deferred);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(deferred.Count, Is.EqualTo(1), "the exit signature is deferred");
            if (validSignature)
            {
                Assert.That(() => process(null), Throws.Nothing, "fixture bug: the serial path must accept the exit");
                Assert.That(() => BlockSignatureBatch.Run(batch => process(batch)), Throws.Nothing);
            }
            else
            {
                Assert.That(() => process(null), Throws.TypeOf<BeaconStateException>().With.Message.EqualTo(refusal));
                Assert.That(() => BlockSignatureBatch.Run(batch => process(batch)), Throws.TypeOf<BeaconStateException>().With.Message.EqualTo(refusal));
            }
        }
    }

    // ---- The proposer signature, verified ahead of the block's batch through the state transition ----

    private static SignedBeaconBlockGloas SignedByProposer(BeaconBlockGloas block)
    {
        BeaconStateGloas pre = SharedGloas.Value.Pre;
        Hash256 domain = pre.GetDomain(DomainType.BeaconProposer, BeaconStateAccessors.ComputeEpochAtSlot(block.Slot));
        return new SignedBeaconBlockGloas { Message = block, Signature = Sign(ValidatorKey((int)block.ProposerIndex), Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(block), domain)) };
    }

    private static SignedBeaconBlock SignedFuluBlockByProposer(BeaconBlock block)
    {
        BeaconStateFulu pre = SharedFulu.Value.Anchor.Clone();
        SlotProcessing.ProcessSlots(pre, block.Slot, new EpochCache());
        Hash256 domain = pre.GetDomain(DomainType.BeaconProposer, BeaconStateAccessors.ComputeEpochAtSlot(block.Slot));
        return new SignedBeaconBlock { Message = block, Signature = Sign(ValidatorKey((int)block.ProposerIndex), Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(block), domain)) };
    }

    private static string? ApplyGloasBlock(SignedBeaconBlockGloas signedBlock)
    {
        GloasFixture fixture = SharedGloas.Value;
        return Refusal(() => ForkedStateTransition.Apply(new ForkedBeaconState.OfGloas(fixture.BeforeSlots.Clone()), new ForkedSignedBeaconBlock.OfGloas(signedBlock), new EpochCache(), fixture.Pubkeys, new AcceptingNotifier(), UpgradeEpochSpec(), validateResult: false));
    }

    private sealed class CountingNotifier : INewPayloadNotifier
    {
        public int Calls { get; private set; }

        public ExecutionStatus NotifyNewPayload(BeaconBlockBody body)
        {
            Calls++;
            return ExecutionStatus.Valid;
        }
    }

    private static string? ApplyFuluBlock(SignedBeaconBlock signedBlock, INewPayloadNotifier? notifier = null)
    {
        FuluFixture fixture = SharedFulu.Value;
        return Refusal(() => FuluStateTransition.Apply(fixture.Anchor.Clone(), signedBlock, new EpochCache(), fixture.Pubkeys, notifier ?? new AcceptingNotifier(), fixture.Chain.Spec, validateResult: false));
    }

    [Test]
    public void A_proposer_signed_block_is_accepted_through_the_state_transition()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ApplyGloasBlock(SignedByProposer(SignedGloasBlock())), Is.Null, "gloas");
            Assert.That(ApplyFuluBlock(SignedFuluBlockByProposer(SignedFuluBlock())), Is.Null, "fulu");
        }
    }

    /// <summary>
    /// A block nobody signed must cost one pairing, not a whole <c>process_block</c>: gossip hands it to the
    /// state transition unverified, so the state must be exactly as slot processing left it.
    /// </summary>
    [Test]
    public void A_forged_gloas_proposer_signature_is_refused_before_process_block_touches_the_state()
    {
        GloasFixture fixture = SharedGloas.Value;
        BeaconStateGloas state = fixture.BeforeSlots.Clone();
        SignedBeaconBlockGloas signedBlock = new() { Message = SignedGloasBlock(), Signature = WrongSignature };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => ForkedStateTransition.Apply(new ForkedBeaconState.OfGloas(state), new ForkedSignedBeaconBlock.OfGloas(signedBlock), new EpochCache(), fixture.Pubkeys, new AcceptingNotifier(), UpgradeEpochSpec(), validateResult: false),
                Throws.TypeOf<ProposerSignatureException>().With.Message.EqualTo($"Invalid proposer signature for the block at slot {BlockSlot}"));
            Assert.That(SszRoots.HashTreeRoot(state), Is.EqualTo(SszRoots.HashTreeRoot(fixture.Pre)));
        }
    }

    /// <summary>
    /// <c>verify_block_signature</c> runs before <c>process_block</c>, so a bad proposer signature is the refusal
    /// whatever else in the block is bad.
    /// </summary>
    [Test]
    public void A_bad_gloas_proposer_signature_is_refused_ahead_of_everything_the_block_does([Values] bool badOperationSignature, [Values] bool failingOperation)
    {
        BeaconBlockGloas block = SignedGloasBlock();
        if (badOperationSignature)
            Replace(block, SignedPart.Attestation, WrongSignature);
        if (failingOperation)
            block.Body!.PayloadAttestations![0].Data!.Slot = ParentSlot - 1;

        SignedBeaconBlockGloas signedBlock = new() { Message = block, Signature = WrongSignature };

        Assert.That(ApplyGloasBlock(signedBlock), Is.EqualTo($"Invalid proposer signature for the block at slot {BlockSlot}"));
    }

    /// <summary>
    /// Fulu <c>process_block</c> sends the payload to the execution layer, so the proposer signature is verified
    /// before it, not deferred: a block no proposer signed must never reach the execution layer.
    /// </summary>
    [Test]
    public void A_bad_fulu_proposer_signature_is_refused_before_the_payload_reaches_the_execution_layer([Values] bool badRandao, [Values] bool failingAttestation)
    {
        BeaconBlock block = SignedFuluBlock();
        if (badRandao)
            block.Body!.RandaoReveal = WrongSignature;
        if (failingAttestation)
            block.Body!.Attestations = [StructurallyInvalidAttestation()];

        SignedBeaconBlock signedBlock = new() { Message = block, Signature = WrongSignature };
        CountingNotifier notifier = new();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ApplyFuluBlock(signedBlock, notifier), Is.EqualTo($"Invalid proposer signature for the block at slot {FuluBlockSlot}"));
            Assert.That(notifier.Calls, Is.Zero);
        }
    }

    [Test]
    public void A_bad_operation_signature_behind_a_valid_proposer_signature_is_refused_through_the_state_transition()
    {
        BeaconBlockGloas gloasBlock = SignedGloasBlock();
        Replace(gloasBlock, SignedPart.SyncAggregate, WrongSignature);
        BeaconBlock fuluBlock = SignedFuluBlock();
        fuluBlock.Body!.RandaoReveal = WrongSignature;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ApplyGloasBlock(SignedByProposer(gloasBlock)), Is.EqualTo(SerialRefusal(SignedPart.SyncAggregate)), "gloas");
            Assert.That(ApplyFuluBlock(SignedFuluBlockByProposer(fuluBlock)), Is.EqualTo(SerialRefusal(SignedPart.Randao)), "fulu");
        }
    }

    /// <summary>
    /// The state transition verifies a block's signatures together at the block's end, so a bad RANDAO reveal is refused
    /// only after the rest of the block ran and the reveal was mixed in; a check at its own step would stop before the mix.
    /// </summary>
    [Test]
    public void A_bad_randao_reveal_is_refused_after_the_rest_of_the_block_ran_through_the_state_transition()
    {
        BeaconBlockGloas gloasBlock = SignedGloasBlock();
        gloasBlock.Body!.RandaoReveal = WrongSignature;
        BeaconStateGloas gloasState = SharedGloas.Value.BeforeSlots.Clone();
        int gloasMix = (int)(BeaconStateAccessors.ComputeEpochAtSlot(BlockSlot) % Presets.EpochsPerHistoricalVector);
        string? gloasRefusal = Refusal(() => ForkedStateTransition.Apply(new ForkedBeaconState.OfGloas(gloasState), new ForkedSignedBeaconBlock.OfGloas(SignedByProposer(gloasBlock)),
            new EpochCache(), SharedGloas.Value.Pubkeys, new AcceptingNotifier(), UpgradeEpochSpec(), validateResult: false));

        BeaconBlock fuluBlock = SignedFuluBlock();
        fuluBlock.Body!.RandaoReveal = WrongSignature;
        FuluFixture fulu = SharedFulu.Value;
        BeaconStateFulu fuluState = fulu.Anchor.Clone();
        int fuluMix = (int)(BeaconStateAccessors.ComputeEpochAtSlot(FuluBlockSlot) % Presets.EpochsPerHistoricalVector);
        string? fuluRefusal = Refusal(() => FuluStateTransition.Apply(fuluState, SignedFuluBlockByProposer(fuluBlock), new EpochCache(), fulu.Pubkeys, new AcceptingNotifier(), fulu.Chain.Spec, validateResult: false));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(gloasRefusal, Is.EqualTo(SerialRefusal(SignedPart.Randao)), "gloas");
            Assert.That(gloasState.RandaoMixes![gloasMix], Is.EqualTo(Mixed(SharedGloas.Value.Pre.RandaoMixes![gloasMix], WrongSignature)), "gloas mix");
            Assert.That(fuluRefusal, Is.EqualTo(SerialRefusal(SignedPart.Randao)), "fulu");
            Assert.That(fuluState.RandaoMixes![fuluMix], Is.EqualTo(Mixed(fulu.Anchor.RandaoMixes![fuluMix], WrongSignature)), "fulu mix");
        }
    }

    private static string? Refusal(Action apply)
    {
        try
        {
            apply();
        }
        catch (BeaconStateException e)
        {
            return e.Message;
        }

        return null;
    }

    private static Hash256 Mixed(Hash256 mix, BlsSignature reveal)
    {
        byte[] mixed = SHA256.HashData(reveal.Bytes);
        for (int i = 0; i < mixed.Length; i++)
        {
            mixed[i] ^= mix.Bytes[i];
        }
        return new Hash256(mixed);
    }

    private static Hash256 EpochRoot(ulong epoch)
    {
        byte[] root = new byte[32];
        BinaryPrimitives.WriteUInt64LittleEndian(root, epoch);
        return new Hash256(root);
    }
}
