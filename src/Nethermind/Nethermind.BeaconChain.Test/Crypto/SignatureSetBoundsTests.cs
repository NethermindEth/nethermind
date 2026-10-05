// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Collections;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Test.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using static Nethermind.BeaconChain.Test.StateTransition.GloasTestFixtures;

namespace Nethermind.BeaconChain.Test.Crypto;

[TestFixture]
[HardTimeout(60_000)]
public class SignatureSetBoundsTests
{
    private const int KeyCount = 4;
    private const ulong Wrap = 1UL << 32;

    public enum Verifier
    {
        FuluHeader,
        FuluExit,
        FuluAttestation,
        GloasHeader,
        GloasExit,
        GloasAttestation,
        GloasPayloadAttestation,
    }

    // Include uint64 indices whose low 32 bits alias valid validators; honest signatures make truncation observable.
    private static readonly ulong[] Indices = [KeyCount - 1, KeyCount, KeyCount + 1, uint.MaxValue, Wrap, Wrap + 1, ulong.MaxValue];

    private static readonly Lazy<PubkeyCache> LargeCache = new(BuildLargeCache);

    [Test]
    public void An_index_outside_the_registry_is_refused_without_a_fault([Values] Verifier verifier, [ValueSource(nameof(Indices))] ulong index)
    {
        // An out-of-range index is signed by a validator whose key its low 32 bits would select, else by validator 0.
        ulong low = index % Wrap;
        int signer = low < KeyCount ? (int)low : 0;

        Assert.That(() => Verify(verifier, index, signer), Is.EqualTo(index < KeyCount));
    }

    [TestCase(KeyCount - 1, ExpectedResult = true)]
    [TestCase(KeyCount, ExpectedResult = false)]
    [TestCase(KeyCount + 1, ExpectedResult = false)]
    [TestCase(int.MaxValue, ExpectedResult = false)]
    [TestCase(-1, ExpectedResult = false)]
    [TestCase(int.MinValue, ExpectedResult = false)]
    public bool A_randao_reveal_by_an_index_outside_the_registry_is_refused_without_a_fault(int proposerIndex)
    {
        BeaconStateFulu state = CreateFuluState(KeyCount);
        BlsSignature reveal = Sign(ValidatorKey(proposerIndex is >= 0 and < KeyCount ? proposerIndex : 0), Domains.ComputeSigningRoot(ImportableBlobBlock.EpochRoot(3), state.GetDomain(DomainType.Randao, 3)));

        return SignatureSets.VerifyRandaoReveal(state, proposerIndex, 3, reveal, Cache());
    }

    [TestCase(KeyCount, ExpectedResult = false)]
    [TestCase(KeyCount + 1, ExpectedResult = true)]
    public bool An_exit_by_a_validator_missing_from_the_key_cache_is_refused_without_a_fault(int cachedKeys)
    {
        BeaconStateFulu state = CreateFuluState(KeyCount + 1);
        Hash256 domain = Domains.ComputeDomain(DomainType.VoluntaryExit, BeaconChainSpec.ForGenesisValidatorsRoot(state.GenesisValidatorsRoot!).CapellaForkVersion, state.GenesisValidatorsRoot!);
        VoluntaryExit exit = new() { Epoch = 1, ValidatorIndex = KeyCount };
        SignedVoluntaryExit signed = new() { Message = exit, Signature = Sign(ValidatorKey(KeyCount), Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(exit), domain)) };

        return SignatureSets.VerifyVoluntaryExit(state, signed, Cache(cachedKeys));
    }

    [Test]
    public void An_attestation_naming_more_validators_than_a_slot_holds_is_refused([Values] bool gloas, [Values(SignatureSets.MaxAttestingIndices, SignatureSets.MaxAttestingIndices + 1)] int count)
    {
        PubkeyCache pubkeys = LargeCache.Value;
        AttestationData data = Vote(1, 0, 1, 0x40);
        ulong[] indices = [.. Enumerable.Range(0, count).Select(static i => (ulong)i)];
        BeaconStateFulu fuluState = CreateFuluState(KeyCount);
        BeaconStateGloas gloasState = CreateGloasState(out _, out _);
        Hash256 domain = gloas ? gloasState.GetDomain(DomainType.BeaconAttester, 1) : fuluState.GetDomain(DomainType.BeaconAttester, 1);
        BlsSignature signature = SumOfMultiples(Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(data), domain), (ulong)count * ((ulong)count + 1) / 2);

        bool verified = gloas
            ? GloasSignatureSets.VerifyIndexedAttestation(gloasState, new IndexedAttestationGloas { AttestingIndices = indices, Data = data, Signature = signature }, pubkeys)
            : SignatureSets.VerifyIndexedAttestation(fuluState, new IndexedAttestation { AttestingIndices = indices, Data = data, Signature = signature }, pubkeys);

        Assert.That(verified, Is.EqualTo(count <= SignatureSets.MaxAttestingIndices));
    }

    // PTC_SIZE sampling with replacement legitimately repeats validators; exceeding it does not.
    [TestCase(512, ExpectedResult = true)]
    [TestCase(513, ExpectedResult = false)]
    [TestCase(2048, ExpectedResult = false)]
    public bool A_payload_attestation_naming_more_members_than_the_committee_holds_is_refused(int count)
    {
        BeaconStateGloas state = CreateGloasState(out _, out _);
        PayloadAttestationData data = new() { BeaconBlockRoot = Hash(0x41), Slot = 1, PayloadPresent = true, BlobDataAvailable = true };
        int[] signers = [.. Enumerable.Range(0, count).Select(static i => i % KeyCount)];
        Hash256 signingRoot = Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(data), state.GetDomain(DomainType.PtcAttester, BeaconStateAccessors.ComputeEpochAtSlot(data.Slot)));
        IndexedPayloadAttestation attestation = new() { AttestingIndices = [.. signers.Select(static i => (ulong)i)], Data = data, Signature = AggregateSignature(signingRoot, signers) };

        return GloasSignatureSets.VerifyIndexedPayloadAttestation(state, attestation, Cache());
    }

    public enum Resized
    {
        Bits,
        Committee,
        Indices,
    }

    [Test]
    public void A_sync_aggregate_over_inputs_that_are_not_committee_sized_is_refused([Values] Resized resized, [Values(0, 511, 512, 513, 2048)] int length)
    {
        BeaconStateFulu state = CreateFuluState(KeyCount);
        Validator[] validators = state.Validators!;
        for (int i = 0; i < validators.Length; i++)
        {
            Validator updated = validators[i].Clone();
            updated.Pubkey = new BlsPublicKey(new Bls.P1(ValidatorKey(i)).Compress());
            validators[i] = updated;
        }

        state.Slot = 1;
        state.CurrentSyncCommittee = new SyncCommittee { Pubkeys = Committee(validators, resized == Resized.Committee ? length : Presets.SyncCommitteeSize), AggregatePubkey = Pubkey(0x60) };
        Hash256 signingRoot = Domains.ComputeSigningRoot(state.GetBlockRootAtSlot(0), state.GetDomain(DomainType.SyncCommittee, 0));
        SyncAggregate aggregate = new()
        {
            SyncCommitteeBits = new BitArray(resized == Resized.Bits ? length : Presets.SyncCommitteeSize, true),
            SyncCommitteeSignature = AggregateSignature(signingRoot, [.. Enumerable.Range(0, Presets.SyncCommitteeSize).Select(static i => i % KeyCount)]),
        };
        int[] committeeIndices = [.. Enumerable.Range(0, resized == Resized.Indices ? length : Presets.SyncCommitteeSize).Select(static i => i % KeyCount)];
        PubkeyCache pubkeys = new();
        pubkeys.Build(validators);

        Assert.That(() => SignatureSets.VerifySyncAggregate(state, aggregate, committeeIndices, pubkeys), Is.EqualTo(length == Presets.SyncCommitteeSize));
    }

    private static BlsPublicKey[] Committee(Validator[] validators, int length) =>
        [.. Enumerable.Range(0, length).Select(i => validators[i % KeyCount].Pubkey)];

    private static PubkeyCache Cache(int count = KeyCount)
    {
        PubkeyCache cache = new();
        cache.Build([.. Enumerable.Range(0, count).Select(static i => new Validator { Pubkey = new BlsPublicKey(new Bls.P1(ValidatorKey(i)).Compress()) })]);
        return cache;
    }

    // Key i = (i+1)*key 0, so indices 0..n-1 sum to n(n+1)/2*key 0.
    private static PubkeyCache BuildLargeCache()
    {
        Bls.P1Affine key = new Bls.P1(ValidatorKey(0)).ToAffine();
        Bls.P1 multiple = new(new long[Bls.P1.Sz]);
        Validator[] validators = new Validator[SignatureSets.MaxAttestingIndices + 1];
        for (int i = 0; i < validators.Length; i++)
        {
            multiple.Add(key);
            validators[i] = new Validator { Pubkey = new BlsPublicKey(multiple.Compress()) };
        }

        PubkeyCache cache = new();
        cache.Build(validators);
        return cache;
    }

    private static BlsSignature SumOfMultiples(Hash256 signingRoot, ulong multiple)
    {
        byte[] scalar = new byte[32];
        BinaryPrimitives.WriteUInt64LittleEndian(scalar, multiple);
        Bls.P2 point = new(new long[Bls.P2.Sz]);
        point.Decode(Sign(ValidatorKey(0), signingRoot).Bytes);
        point.Mult(scalar);
        return new BlsSignature(point.Compress());
    }

    private static bool Verify(Verifier verifier, ulong index, int signer)
    {
        PubkeyCache pubkeys = Cache();
        BeaconStateFulu fulu = CreateFuluState(KeyCount);
        BeaconStateGloas gloas = CreateGloasState(out _, out _);
        Hash256 exitDomain = Domains.ComputeDomain(DomainType.VoluntaryExit, BeaconChainSpec.ForGenesisValidatorsRoot(fulu.GenesisValidatorsRoot!).CapellaForkVersion, fulu.GenesisValidatorsRoot!);
        BeaconBlockHeader header = new() { Slot = 1, ProposerIndex = index, ParentRoot = Hash(0x11), StateRoot = Hash(0x12), BodyRoot = Hash(0x13) };
        VoluntaryExit exit = new() { Epoch = 1, ValidatorIndex = index };
        AttestationData vote = Vote(1, 0, 1, 0x40);
        PayloadAttestationData payload = new() { BeaconBlockRoot = Hash(0x41), Slot = 1, PayloadPresent = true, BlobDataAvailable = true };

        SignedBeaconBlockHeader SignedHeader(Hash256 domain) => new() { Message = header, Signature = Sign(ValidatorKey(signer), Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(header), domain)) };
        SignedVoluntaryExit SignedExit() => new() { Message = exit, Signature = Sign(ValidatorKey(signer), Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(exit), exitDomain)) };
        BlsSignature Aggregate(Hash256 messageRoot, Hash256 domain) => Sign(ValidatorKey(signer), Domains.ComputeSigningRoot(messageRoot, domain));

        return verifier switch
        {
            Verifier.FuluHeader => SignatureSets.VerifySignedBeaconBlockHeader(fulu, SignedHeader(fulu.GetDomain(DomainType.BeaconProposer, 0)), pubkeys),
            Verifier.FuluExit => SignatureSets.VerifyVoluntaryExit(fulu, SignedExit(), pubkeys),
            Verifier.FuluAttestation => SignatureSets.VerifyIndexedAttestation(fulu, new IndexedAttestation { AttestingIndices = [index], Data = vote, Signature = Aggregate(SszRoots.HashTreeRoot(vote), fulu.GetDomain(DomainType.BeaconAttester, 1)) }, pubkeys),
            Verifier.GloasHeader => GloasSignatureSets.VerifySignedBeaconBlockHeader(gloas, SignedHeader(gloas.GetDomain(DomainType.BeaconProposer, 0)), pubkeys),
            Verifier.GloasExit => GloasSignatureSets.VerifyVoluntaryExit(gloas, SignedExit(), pubkeys),
            Verifier.GloasAttestation => GloasSignatureSets.VerifyIndexedAttestation(gloas, new IndexedAttestationGloas { AttestingIndices = [index], Data = vote, Signature = Aggregate(SszRoots.HashTreeRoot(vote), gloas.GetDomain(DomainType.BeaconAttester, 1)) }, pubkeys),
            _ => GloasSignatureSets.VerifyIndexedPayloadAttestation(gloas, new IndexedPayloadAttestation { AttestingIndices = [index], Data = payload, Signature = Aggregate(SszRoots.HashTreeRoot(payload), gloas.GetDomain(DomainType.PtcAttester, 0)) }, pubkeys),
        };
    }
}
