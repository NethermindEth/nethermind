// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Test.P2P;
using Nethermind.BeaconChain.Test.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using static Nethermind.BeaconChain.Test.StateTransition.GloasTestFixtures;

namespace Nethermind.BeaconChain.Test.Crypto;

[TestFixture]
[HardTimeout(60_000)]
public class CachedKeySubgroupTests
{
    private const int KeyCount = 4;
    private const int SignerIndex = 1;

    private static PubkeyCache Cache(bool offSubgroup, bool negateTorsion = false, int torsionIndex = SignerIndex)
    {
        Validator[] validators = [.. Enumerable.Range(0, KeyCount).Select(static i => new Validator { Pubkey = new BlsPublicKey(new Bls.P1(ValidatorKey(i)).Compress()) })];
        if (offSubgroup)
            validators[torsionIndex].Pubkey = OffSubgroupKeys.WithTorsion(ValidatorKey(torsionIndex), negateTorsion);
        PubkeyCache cache = new();
        cache.Build(validators);
        return cache;
    }

    // Opposite torsion cancels in the sum; individual KeyValidate checks are still required.
    private static PubkeyCache CancellingTorsionCache()
    {
        Validator[] validators = [.. Enumerable.Range(0, KeyCount).Select(static i => new Validator { Pubkey = new BlsPublicKey(new Bls.P1(ValidatorKey(i)).Compress()) })];
        validators[0].Pubkey = OffSubgroupKeys.WithTorsion(ValidatorKey(0));
        validators[1].Pubkey = OffSubgroupKeys.WithTorsion(ValidatorKey(1), negateTorsion: true);
        PubkeyCache cache = new();
        cache.Build(validators);
        return cache;
    }

    private static BlsSignature SignBy(int validatorIndex, Hash256 domain, Hash256 messageRoot) =>
        Sign(ValidatorKey(validatorIndex), Domains.ComputeSigningRoot(messageRoot, domain));

    private static SignedBeaconBlockHeader Header(Hash256 domain, int proposerIndex)
    {
        BeaconBlockHeader header = new() { Slot = 1, ProposerIndex = (ulong)proposerIndex, ParentRoot = Hash(0x11), StateRoot = Hash(0x12), BodyRoot = Hash(0x13) };
        return new SignedBeaconBlockHeader { Message = header, Signature = SignBy(proposerIndex, domain, SszRoots.HashTreeRoot(header)) };
    }

    private static Hash256 ExitDomain(Hash256 genesisValidatorsRoot) =>
        Domains.ComputeDomain(DomainType.VoluntaryExit, BeaconChainSpec.ForGenesisValidatorsRoot(genesisValidatorsRoot).CapellaForkVersion, genesisValidatorsRoot);

    private static SignedVoluntaryExit Exit(Hash256 genesisValidatorsRoot, int validatorIndex)
    {
        VoluntaryExit exit = new() { Epoch = 1, ValidatorIndex = (ulong)validatorIndex };
        return new SignedVoluntaryExit { Message = exit, Signature = SignBy(validatorIndex, ExitDomain(genesisValidatorsRoot), SszRoots.HashTreeRoot(exit)) };
    }

    [Test]
    public void Fulu_proposer_signature_under_a_key_outside_the_subgroup_is_refused([Values] bool offSubgroup)
    {
        BeaconStateFulu state = CreateFuluState(KeyCount);
        BeaconBlock block = TestChain.CreateBlock(1, Hash(1)).Message!;
        block.ProposerIndex = SignerIndex;
        BlsSignature signature = SignBy(SignerIndex, state.GetDomain(DomainType.BeaconProposer, 0), SszRoots.HashTreeRoot(block));

        Assert.That(SignatureSets.VerifyProposerSignature(state, block, signature, Cache(offSubgroup)), Is.EqualTo(!offSubgroup));
    }

    [Test]
    public void Fulu_block_header_signature_under_a_key_outside_the_subgroup_is_refused([Values] bool offSubgroup)
    {
        BeaconStateFulu state = CreateFuluState(KeyCount);
        SignedBeaconBlockHeader header = Header(state.GetDomain(DomainType.BeaconProposer, 0), SignerIndex);

        Assert.That(SignatureSets.VerifySignedBeaconBlockHeader(state, header, Cache(offSubgroup)), Is.EqualTo(!offSubgroup));
    }

    [Test]
    public void Fulu_randao_reveal_under_a_key_outside_the_subgroup_is_refused([Values] bool offSubgroup)
    {
        BeaconStateFulu state = CreateFuluState(KeyCount);
        BlsSignature reveal = SignBy(SignerIndex, state.GetDomain(DomainType.Randao, 3), ImportableBlobBlock.EpochRoot(3));

        Assert.That(SignatureSets.VerifyRandaoReveal(state, SignerIndex, 3, reveal, Cache(offSubgroup)), Is.EqualTo(!offSubgroup));
    }

    [Test]
    public void Fulu_voluntary_exit_under_a_key_outside_the_subgroup_is_refused([Values] bool offSubgroup)
    {
        BeaconStateFulu state = CreateFuluState(KeyCount);

        Assert.That(SignatureSets.VerifyVoluntaryExit(state, Exit(state.GenesisValidatorsRoot!, SignerIndex), Cache(offSubgroup)), Is.EqualTo(!offSubgroup));
    }

    [TestCase(false, false, 1, ExpectedResult = true, TestName = "honest_keys_verify")]
    [TestCase(true, false, 1, ExpectedResult = false, TestName = "one_key_outside_the_subgroup_among_several")]
    [TestCase(true, false, 0, ExpectedResult = false, TestName = "first_key_outside_the_subgroup")]
    [TestCase(true, true, 1, ExpectedResult = false, TestName = "torsion_cancelling_across_two_keys")]
    public bool Fulu_attestation_aggregate_over_a_key_outside_the_subgroup_is_refused(bool offSubgroup, bool cancelling, int torsionIndex)
    {
        BeaconStateFulu state = CreateFuluState(KeyCount);
        AttestationData data = Vote(1, 0, 1, 0x40);
        int[] signers = cancelling ? [0, 1] : [0, 1, 2];
        Hash256 domain = state.GetDomain(DomainType.BeaconAttester, data.Target!.Epoch);
        IndexedAttestation attestation = new()
        {
            AttestingIndices = [.. signers.Select(static i => (ulong)i)],
            Data = data,
            Signature = AggregateSignature(Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(data), domain), signers),
        };

        PubkeyCache cache = cancelling ? CancellingTorsionCache() : Cache(offSubgroup, torsionIndex: torsionIndex);

        return SignatureSets.VerifyIndexedAttestation(state, attestation, cache);
    }

    [Test]
    public void Gloas_attestation_aggregate_over_a_key_outside_the_subgroup_is_refused([Values] bool offSubgroup)
    {
        BeaconStateGloas state = CreateGloasState(out _, out _);
        IndexedAttestationGloas attestation = SignedIndexedAttestation(state, Vote(1, 0, 1, 0x40), [0, 1, 2]);

        Assert.That(GloasSignatureSets.VerifyIndexedAttestation(state, attestation, Cache(offSubgroup)), Is.EqualTo(!offSubgroup));
    }

    [Test]
    public void Gloas_payload_attestation_over_a_key_outside_the_subgroup_is_refused([Values] bool offSubgroup)
    {
        BeaconStateGloas state = CreateGloasState(out _, out _);
        PayloadAttestationData data = new() { BeaconBlockRoot = Hash(0x41), Slot = 1, PayloadPresent = true, BlobDataAvailable = true };
        int[] signers = [0, 1, 1, 2];
        Hash256 domain = state.GetDomain(DomainType.PtcAttester, BeaconStateAccessors.ComputeEpochAtSlot(data.Slot));
        IndexedPayloadAttestation attestation = new()
        {
            AttestingIndices = [.. signers.Select(static i => (ulong)i)],
            Data = data,
            Signature = AggregateSignature(Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(data), domain), signers),
        };

        Assert.That(GloasSignatureSets.VerifyIndexedPayloadAttestation(state, attestation, Cache(offSubgroup)), Is.EqualTo(!offSubgroup));
    }

    [Test]
    public void Gloas_block_header_signature_under_a_key_outside_the_subgroup_is_refused([Values] bool offSubgroup)
    {
        BeaconStateGloas state = CreateGloasState(out _, out _);
        SignedBeaconBlockHeader header = SignedHeader(state, 1, SignerIndex, Hash(0x13));

        Assert.That(GloasSignatureSets.VerifySignedBeaconBlockHeader(state, header, Cache(offSubgroup)), Is.EqualTo(!offSubgroup));
    }

    [Test]
    public void Gloas_voluntary_exit_under_a_key_outside_the_subgroup_is_refused([Values] bool offSubgroup)
    {
        BeaconStateGloas state = CreateGloasState(out _, out _);

        Assert.That(GloasSignatureSets.VerifyVoluntaryExit(state, Exit(state.GenesisValidatorsRoot!, SignerIndex), Cache(offSubgroup)), Is.EqualTo(!offSubgroup));
    }

    [Test]
    public void Gloas_block_proposer_signature_under_a_key_outside_the_subgroup_is_refused([Values] bool offSubgroup)
    {
        BeaconStateGloas state = CreateGloasState(out _, out _);
        SignedBeaconBlockGloas block = MinimalBlock(state, SelfBuildBid(state, state.LatestBlockHash!, Hash(0x9B)));
        block.Message!.ProposerIndex = SignerIndex;
        Hash256 domain = state.GetDomain(DomainType.BeaconProposer, BeaconStateAccessors.ComputeEpochAtSlot(block.Message.Slot));
        block.Signature = SignBy(SignerIndex, domain, SszRoots.HashTreeRoot(block.Message));

        Assert.That(GloasBlockProcessing.VerifyProposerSignature(state, block, Cache(offSubgroup)), Is.EqualTo(!offSubgroup));
    }

    [Test]
    public void Gloas_randao_reveal_under_a_key_outside_the_subgroup_is_refused([Values] bool offSubgroup)
    {
        BeaconStateGloas state = CreateGloasState(out _, out _);
        int proposerIndex = (int)state.GetBeaconProposerIndex();
        ulong epoch = state.GetCurrentEpoch();
        BeaconBlockBodyGloas body = new() { RandaoReveal = SignBy(proposerIndex, state.GetDomain(DomainType.Randao, epoch), ImportableBlobBlock.EpochRoot(epoch)) };
        PubkeyCache cache = Cache(offSubgroup, torsionIndex: proposerIndex);

        Action process = () => GloasBlockProcessing.ProcessRandao(state, body, cache);

        if (offSubgroup)
            Assert.That(process, Throws.TypeOf<BeaconStateException>().With.Message.EqualTo("Invalid RANDAO reveal"));
        else
            Assert.That(process, Throws.Nothing);
    }

    [Test]
    public void Self_build_envelope_signature_under_a_key_outside_the_subgroup_is_refused([Values] bool offSubgroup)
    {
        BeaconStateGloas state = CreateGloasState(out _, out _);
        SignedExecutionPayloadBid bid = SelfBuildBid(state, state.LatestBlockHash!, Hash(0x9A));
        GloasBlockProcessing.ProcessExecutionPayloadBid(state, bid, SyntheticSpec(), new PubkeyCache(), verifySignature: true);
        int proposerIndex = (int)state.LatestBlockHeader!.ProposerIndex;
        SignedExecutionPayloadEnvelope envelope = ValidEnvelope(state, bid.Message!, ValidatorKey(proposerIndex), Presets.BuilderIndexSelfBuild);
        PubkeyCache cache = Cache(offSubgroup, torsionIndex: proposerIndex);

        Action verify = () => GloasBlockProcessing.VerifyExecutionPayloadEnvelope(new BlockStates().Add(state), envelope, new AcceptingNotifier(), cache);

        if (offSubgroup)
            Assert.That(verify, Throws.TypeOf<BeaconStateException>().With.Message.EqualTo("Invalid execution payload envelope signature"));
        else
            Assert.That(verify, Throws.Nothing);
    }

    // Every cached slot aliases validator 0's key, so only the index bound can refuse an out-of-range alias.
    private static PubkeyCache SameKeyCache()
    {
        Validator[] validators = [.. Enumerable.Range(0, KeyCount).Select(static _ => new Validator { Pubkey = new BlsPublicKey(new Bls.P1(ValidatorKey(0)).Compress()) })];
        PubkeyCache cache = new();
        cache.Build(validators);
        return cache;
    }

    private static void ProcessRandaoAt(ulong proposerIndex)
    {
        BeaconStateGloas state = CreateGloasState(out _, out _);
        state.ProposerLookahead![(int)(state.Slot % Presets.SlotsPerEpoch)] = proposerIndex;
        BeaconBlockBodyGloas body = new() { RandaoReveal = SignBy(0, state.GetDomain(DomainType.Randao, state.GetCurrentEpoch()), ImportableBlobBlock.EpochRoot(state.GetCurrentEpoch())) };
        GloasBlockProcessing.ProcessRandao(state, body, SameKeyCache());
    }

    private static void VerifyEnvelopeAt(ulong proposerIndex)
    {
        BeaconStateGloas state = CreateGloasState(out _, out _);
        SignedExecutionPayloadBid bid = SelfBuildBid(state, state.LatestBlockHash!, Hash(0x9A));
        GloasBlockProcessing.ProcessExecutionPayloadBid(state, bid, SyntheticSpec(), new PubkeyCache(), verifySignature: true);
        state.LatestBlockHeader!.ProposerIndex = proposerIndex;
        SignedExecutionPayloadEnvelope envelope = ValidEnvelope(state, bid.Message!, ValidatorKey(0), Presets.BuilderIndexSelfBuild);
        GloasBlockProcessing.VerifyExecutionPayloadEnvelope(new BlockStates().Add(state), envelope, new AcceptingNotifier(), SameKeyCache());
    }

    [Test]
    public void Gloas_randao_reveal_for_a_proposer_index_outside_the_key_cache_is_refused(
        [Values((ulong)KeyCount, (ulong)KeyCount + 1, 2048UL, 1UL << 31, 1UL << 32, ulong.MaxValue)] ulong proposerIndex) =>
        Assert.That(() => ProcessRandaoAt(proposerIndex), Throws.TypeOf<BeaconStateException>().With.Message.EqualTo("Invalid RANDAO reveal"));

    [Test]
    public void Gloas_randao_reveal_for_the_last_cached_proposer_index_is_accepted() =>
        Assert.That(() => ProcessRandaoAt(KeyCount - 1), Throws.Nothing);

    [Test]
    public void Self_build_envelope_for_a_proposer_index_outside_the_key_cache_is_refused(
        [Values((ulong)KeyCount, (ulong)KeyCount + 1, 2048UL, 1UL << 31, 1UL << 32, ulong.MaxValue)] ulong proposerIndex) =>
        Assert.That(() => VerifyEnvelopeAt(proposerIndex), Throws.TypeOf<BeaconStateException>().With.Message.EqualTo("Invalid execution payload envelope signature"));

    [Test]
    public void Self_build_envelope_for_the_last_cached_proposer_index_is_accepted() =>
        Assert.That(() => VerifyEnvelopeAt(KeyCount - 1), Throws.Nothing);

    [Test]
    public void A_refused_key_stays_refused_on_every_later_read()
    {
        PubkeyCache cache = Cache(offSubgroup: true);

        bool[] verdicts = [cache.TryGetValidPublicKey(SignerIndex, out _), cache.TryGetValidPublicKey(SignerIndex, out _), cache.TryGetValidPublicKey(0, out _)];

        Assert.That(verdicts, Is.EqualTo(new[] { false, false, true }));
    }
}
