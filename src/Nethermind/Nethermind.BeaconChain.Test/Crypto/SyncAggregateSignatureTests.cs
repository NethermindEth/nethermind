// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.IO;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using static Nethermind.BeaconChain.Test.StateTransition.GloasTestFixtures;

namespace Nethermind.BeaconChain.Test.Crypto;

[TestFixture]
[HardTimeout(60_000)]
public class SyncAggregateSignatureTests
{
    private const int RegistrySize = 24;

    // Validators whose keys sum to infinity: the second holds the negation of the first.
    private const int KeyIndex = RegistrySize;
    private const int NegatedKeyIndex = RegistrySize + 1;

    // The compression flag is clear, so these 48 bytes never decode as a G1 point.
    private static readonly BlsPublicKey UndecodableKey = Pubkey(0x57);

    private static readonly int[] CacheSizes = [0, 4, RegistrySize + 2, RegistrySize + 12];

    private static int[] RepeatingCommittee() => [.. Enumerable.Range(0, Presets.SyncCommitteeSize).Select(static i => i % 7)];

    [Test]
    public void Cached_aggregate_equals_the_decompressed_participant_aggregate([Values(1, 3, 512)] int stride, [ValueSource(nameof(CacheSizes))] int cachedValidators)
    {
        BeaconStateFulu state = CreateState(RepeatingCommittee(), out _);
        PubkeyCache pubkeys = CacheOf(state, cachedValidators);
        BitArray bits = Bits(i => i % stride == 0);
        BlsPublicKey[] committee = state.CurrentSyncCommittee!.Pubkeys!;

        BlsSigner.AggregatedPublicKey decompressed = new(new long[Bls.P1.Sz]);
        for (int i = 0; i < bits.Length; i++)
        {
            if (bits[i])
                Assert.That(decompressed.TryAggregate(committee[i].Bytes, out _), Is.True, "fixture bug: committee keys decode");
        }

        Bls.P1 cached = new(new long[Bls.P1.Sz]);
        int count = SignatureSets.AggregateSyncParticipants(bits, committee, new EpochCache().GetSyncCommitteeIndices(state.CurrentSyncCommittee, state.Validators!), pubkeys, cached);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(count, Is.EqualTo(Enumerable.Range(0, bits.Length).Count(i => bits[i])));
        Assert.That(cached.ToAffine().Compress(), Is.EqualTo(decompressed.PublicKey.Compress()));
    }

    [Test]
    public void A_valid_aggregate_over_repeated_members_is_accepted_serially_and_batched([ValueSource(nameof(CacheSizes))] int cachedValidators)
    {
        int[] members = RepeatingCommittee();
        BeaconStateFulu state = CreateState(members, out Hash256 signingRoot);
        BitArray bits = Bits(static i => i % 3 != 1);
        BlsSignature signature = AggregateSignature(signingRoot, [.. Enumerable.Range(0, members.Length).Where(i => bits[i]).Select(i => members[i])]);

        Assert.That(Verdicts(state, CacheOf(state, cachedValidators), bits, signature), Is.EqualTo((true, true)));
    }

    [Test]
    public void An_aggregate_missing_one_repetition_is_refused_serially_and_batched()
    {
        int[] members = RepeatingCommittee();
        BeaconStateFulu state = CreateState(members, out Hash256 signingRoot);
        BitArray bits = Bits(static _ => true);
        // Validator 0 sits at 74 positions; signing for 73 of them is a different aggregate.
        BlsSignature signature = AggregateSignature(signingRoot, [.. members.Skip(1)]);

        Assert.That(Verdicts(state, CacheOf(state, RegistrySize), bits, signature), Is.EqualTo((false, false)));
    }

    [TestCase(true, ExpectedResult = true)]
    [TestCase(false, ExpectedResult = false)]
    public bool No_participants_accept_only_the_infinity_signature(bool infinitySignature)
    {
        BeaconStateFulu state = CreateState(RepeatingCommittee(), out Hash256 signingRoot);
        BlsSignature signature = infinitySignature ? new BlsSignature(G2PointAtInfinity()) : Sign(ValidatorKey(0), signingRoot);

        (bool serial, bool batched) = Verdicts(state, CacheOf(state, RegistrySize), Bits(static _ => false), signature);
        Assert.That(batched, Is.EqualTo(serial));
        return serial;
    }

    [Test]
    public void Participants_whose_keys_sum_to_infinity_fail_the_block([Values] bool gloas, [Values] bool batched, [Values] bool infinitySignature)
    {
        int[] members = [.. Enumerable.Range(0, Presets.SyncCommitteeSize).Select(i => gloas ? i % 2 : i % 2 == 0 ? KeyIndex : NegatedKeyIndex)];
        SyncFixture fixture = CreateSyncFixture(gloas, members, keyedValidators: 2, cachedValidators: RegistrySize + 2);
        if (gloas)
        {
            SetPubkey(fixture.Validators, fixture.Committee, members, 1, NegatedKey(0));
            fixture.Pubkeys.Build(fixture.Validators[..2]);
        }
        BitArray bits = Bits(static i => i < 2);
        AssertParticipantsSumToInfinity(bits, fixture.Committee, fixture.Validators, fixture.Pubkeys);
        SyncAggregate aggregate = new() { SyncCommitteeBits = bits, SyncCommitteeSignature = InfinityOrSignedBy(infinitySignature, gloas ? 0 : KeyIndex, fixture.SigningRoot) };

        Assert.That(() => Process(batch => fixture.Process(aggregate, true, batch), batched), Throws.TypeOf<BeaconStateException>().With.Message.EqualTo("Invalid sync aggregate signature"));
    }

    public enum AttestationKind
    {
        Fulu,
        Gloas,
        GloasPayload,
    }

    [Test]
    public void An_attestation_whose_keys_sum_to_infinity_is_refused_serially_and_batched([Values] AttestationKind kind, [Values] bool infinitySignature)
    {
        AttestationData data = new() { Slot = 0, Index = 0, BeaconBlockRoot = Hash256.Zero, Source = new Checkpoint { Epoch = 0, Root = Hash256.Zero }, Target = new Checkpoint { Epoch = 0, Root = Hash256.Zero } };
        Func<BlockSignatureBatch.Deferral?, bool> verify;
        if (kind == AttestationKind.Fulu)
        {
            BeaconStateFulu state = CreateState(RepeatingCommittee(), out _);
            PubkeyCache pubkeys = CacheOf(state, state.Validators!.Length);
            Hash256 signingRoot = Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(data), state.GetDomain(DomainType.BeaconAttester, 0));
            IndexedAttestation attestation = new() { AttestingIndices = [KeyIndex, NegatedKeyIndex], Data = data, Signature = InfinityOrSignedBy(infinitySignature, KeyIndex, signingRoot) };
            verify = deferral => SignatureSets.VerifyIndexedAttestation(state, attestation, pubkeys, deferral);
        }
        else
        {
            int[] members = RepeatingCommittee();
            BeaconStateGloas state = GloasStateWithCommittee(members, 7, out _);
            SetPubkey(state.Validators!, state.CurrentSyncCommittee!, members, 1, NegatedKey(0));
            PubkeyCache pubkeys = new();
            pubkeys.Build(state.Validators![..7]);
            if (kind == AttestationKind.Gloas)
            {
                Hash256 signingRoot = Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(data), state.GetDomain(DomainType.BeaconAttester, 0));
                IndexedAttestationGloas attestation = new() { AttestingIndices = [0, 1], Data = data, Signature = InfinityOrSignedBy(infinitySignature, 0, signingRoot) };
                verify = deferral => GloasSignatureSets.VerifyIndexedAttestation(state, attestation, pubkeys, deferral);
            }
            else
            {
                PayloadAttestationData payloadData = new() { BeaconBlockRoot = Hash256.Zero, Slot = 0 };
                Hash256 signingRoot = Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(payloadData), state.GetDomain(DomainType.PtcAttester, 0));
                IndexedPayloadAttestation attestation = new() { AttestingIndices = [0, 1], Data = payloadData, Signature = InfinityOrSignedBy(infinitySignature, 0, signingRoot) };
                verify = deferral => GloasSignatureSets.VerifyIndexedPayloadAttestation(state, attestation, pubkeys, deferral);
            }
        }

        Assert.That(SerialAndBatched(verify), Is.EqualTo((false, false)));
    }

    [Test]
    public void An_infinity_member_refuses_the_aggregate_it_participates_in([Values(0, 4)] int cachedValidators, [Values] bool participates)
    {
        const int infinityIndex = 6;
        int[] members = RepeatingCommittee();
        BeaconStateFulu state = CreateState(members, out Hash256 signingRoot);
        SetPubkey(state.Validators!, state.CurrentSyncCommittee!, members, infinityIndex, new BlsPublicKey(G1PointAtInfinity()));
        BitArray bits = Bits(i => participates || members[i] != infinityIndex);
        BlsSignature signature = AggregateSignature(signingRoot, [.. Enumerable.Range(0, members.Length).Where(i => bits[i] && members[i] != infinityIndex).Select(i => members[i])]);

        Assert.That(Verdicts(state, CacheOf(state, cachedValidators), bits, signature), Is.EqualTo((!participates, !participates)));
    }

    [TestCase(false, ExpectedResult = true)]
    [TestCase(true, ExpectedResult = false)]
    public bool A_cache_entry_that_differs_from_the_committee_key_does_not_change_the_verdict(bool signedForTheCachedKey)
    {
        const int staleIndex = 5;
        const int cachedKeyIndex = 20;
        int[] members = RepeatingCommittee();
        BeaconStateFulu state = CreateState(members, out Hash256 signingRoot);
        Validator[] stale = [.. state.Validators!];
        stale[staleIndex] = stale[staleIndex].Clone();
        stale[staleIndex].Pubkey = stale[cachedKeyIndex].Pubkey;
        PubkeyCache pubkeys = new();
        pubkeys.Build(stale);
        BitArray bits = Bits(static _ => true);
        BlsSignature signature = AggregateSignature(signingRoot, [.. members.Select(m => signedForTheCachedKey && m == staleIndex ? cachedKeyIndex : m)]);

        (bool serial, bool batched) = Verdicts(state, pubkeys, bits, signature);
        Assert.That(batched, Is.EqualTo(serial));
        return serial;
    }

    [TestCase(true, ExpectedResult = false)]
    [TestCase(false, ExpectedResult = true)]
    public bool An_undecodable_uncached_member_refuses_the_aggregate_it_participates_in(bool undecodableParticipates)
    {
        const int undecodableIndex = RegistrySize - 1;
        int[] members = RepeatingCommittee();
        members[^1] = undecodableIndex;
        BeaconStateFulu state = CreateState(members, out Hash256 signingRoot);
        SetPubkey(state.Validators!, state.CurrentSyncCommittee!, members, undecodableIndex, UndecodableKey);
        BitArray bits = LastMemberBits(undecodableParticipates);

        (bool serial, bool batched) = Verdicts(state, CacheOf(state, 7), bits, HonestSignatureWithoutLastMember(signingRoot, members, bits));
        Assert.That(batched, Is.EqualTo(serial));
        return serial;
    }

    [Test]
    public void A_member_key_outside_the_subgroup_refuses_the_aggregate_serially_and_batched([ValueSource(nameof(CacheSizes))] int cachedValidators)
    {
        const int offSubgroupIndex = 5;
        int[] members = RepeatingCommittee();
        BeaconStateFulu state = CreateState(members, out Hash256 signingRoot);
        SetPubkey(state.Validators!, state.CurrentSyncCommittee!, members, offSubgroupIndex, OffSubgroupKeys.WithTorsion(ValidatorKey(offSubgroupIndex)));
        BitArray bits = Bits(static i => i % 3 != 1);
        Assert.That(Enumerable.Range(0, members.Length).Any(i => bits[i] && members[i] == offSubgroupIndex), Is.True, "fixture bug: the key must participate");
        BlsSignature signature = AggregateSignature(signingRoot, [.. Enumerable.Range(0, members.Length).Where(i => bits[i]).Select(i => members[i])]);

        Assert.That(Verdicts(state, CacheOf(state, cachedValidators), bits, signature), Is.EqualTo((false, false)));
    }

    // Opposite torsion can cancel in the aggregate; validate each participating key.
    [Test]
    public void Members_whose_torsion_cancels_refuse_the_aggregate_serially_and_batched([ValueSource(nameof(CacheSizes))] int cachedValidators)
    {
        const int first = 5;
        const int second = 6;
        int[] members = RepeatingCommittee();
        BeaconStateFulu state = CreateState(members, out Hash256 signingRoot);
        SetPubkey(state.Validators!, state.CurrentSyncCommittee!, members, first, OffSubgroupKeys.WithTorsion(ValidatorKey(first)));
        SetPubkey(state.Validators!, state.CurrentSyncCommittee!, members, second, OffSubgroupKeys.WithTorsion(ValidatorKey(second), negateTorsion: true));
        BitArray bits = Bits(i => members[i] is first or second);

        Assert.That(Verdicts(state, CacheOf(state, cachedValidators), bits, HonestSignature(signingRoot, members, bits)), Is.EqualTo((false, false)));
    }

    [Test]
    public void An_undecodable_uncached_member_refuses_the_Gloas_aggregate_it_participates_in([Values] bool undecodableParticipates)
    {
        const int undecodableIndex = 7;
        int[] members = RepeatingCommittee();
        members[^1] = undecodableIndex;
        SyncFixture fixture = CreateSyncFixture(true, members, keyedValidators: undecodableIndex);
        SetPubkey(fixture.Validators, fixture.Committee, members, undecodableIndex, UndecodableKey);
        BitArray bits = LastMemberBits(undecodableParticipates);
        SyncAggregate aggregate = new() { SyncCommitteeBits = bits, SyncCommitteeSignature = HonestSignatureWithoutLastMember(fixture.SigningRoot, members, bits) };
        Action process = () => fixture.Process(aggregate, true, null);

        if (undecodableParticipates)
            Assert.That(process, Throws.TypeOf<BeaconStateException>().With.Message.EqualTo("Invalid sync aggregate signature"));
        else
            Assert.That(process, Throws.Nothing);
    }

    [Test]
    public void Processing_no_participants_accepts_only_the_infinity_signature([Values] bool gloas, [Values] bool infinitySignature)
    {
        SyncFixture fixture = CreateSyncFixture(gloas, RepeatingCommittee());
        SyncAggregate aggregate = new() { SyncCommitteeBits = Bits(static _ => false), SyncCommitteeSignature = NoParticipantSignature(infinitySignature, fixture.SigningRoot) };
        Action process = () => fixture.Process(aggregate, true, null);

        if (infinitySignature)
            Assert.That(process, Throws.Nothing);
        else
            Assert.That(process, Throws.TypeOf<BeaconStateException>().With.Message.EqualTo("Invalid sync aggregate signature"));
    }

    public enum SignatureKind
    {
        Honest,
        AllZero,
        InfinityWithParticipants,
        NotInG2,
        AllOxff,
        HonestPlusG2Torsion,
    }

    [Test]
    public void Processing_participants_accepts_only_the_honest_signature([Values] SignatureKind kind, [Values] bool batched)
    {
        int[] members = RepeatingCommittee();
        BeaconStateFulu state = CreateState(members, out Hash256 signingRoot);
        BitArray bits = Bits(static i => i % 2 == 0);
        BlsSignature honest = HonestSignature(signingRoot, members, bits);
        BlsSignature signature = kind switch
        {
            SignatureKind.Honest => honest,
            SignatureKind.AllZero => new BlsSignature(new byte[BlsSignature.Length]),
            SignatureKind.InfinityWithParticipants => new BlsSignature(G2PointAtInfinity()),
            SignatureKind.NotInG2 => OffSubgroupKeys.NotInG2Signature(),
            SignatureKind.AllOxff => new BlsSignature(Enumerable.Repeat((byte)0xff, BlsSignature.Length).ToArray()),
            _ => OffSubgroupKeys.WithG2Torsion(honest),
        };
        SyncAggregate syncAggregate = new() { SyncCommitteeBits = bits, SyncCommitteeSignature = signature };
        BlockSignatureBatch? batch = batched ? new BlockSignatureBatch() : null;

        Action process = () =>
        {
            BlockProcessing.ProcessSyncAggregate(state, syncAggregate, new EpochCache(), CacheOf(state, RegistrySize), verifySignature: true, batch);
            batch?.Verify();
        };

        if (kind == SignatureKind.Honest)
            Assert.That(process, Throws.Nothing);
        else
            Assert.That(process, Throws.TypeOf<BeaconStateException>().With.Message.EqualTo("Invalid sync aggregate signature"));
    }

    [Test]
    public void A_valid_aggregate_is_deferred_to_the_batch([Values] bool gloas)
    {
        int[] members = RepeatingCommittee();
        SyncFixture fixture = CreateSyncFixture(gloas, members);
        BitArray bits = Bits(static i => i % 3 != 1);
        SyncAggregate aggregate = new() { SyncCommitteeBits = bits, SyncCommitteeSignature = HonestSignature(fixture.SigningRoot, members, bits) };
        BlockSignatureBatch batch = new();
        fixture.Process(aggregate, true, batch);

        Assert.That(batch.Count, Is.EqualTo(1));
        Assert.That(batch.Verify, Throws.Nothing);
    }

    // Signature refusal precedes registry lookup; a missing member must not hide a bad signature.
    [Test]
    public void A_committee_member_missing_from_the_registry_fails_the_block_after_the_signature_check([Values] bool gloas, [Values] bool verifySignature, [Values] bool batched, [Values] bool participates, [Values] bool honest)
    {
        const int unregisteredPosition = 3;
        const int unregisteredKey = 200;
        int[] members = RepeatingCommittee();
        SyncFixture fixture = CreateSyncFixture(gloas, members);
        fixture.Committee.Pubkeys![unregisteredPosition] = new BlsPublicKey(new Bls.P1(ValidatorKey(unregisteredKey)).Compress());
        BitArray bits = Bits(i => i != unregisteredPosition || participates);
        int[] signers = [.. Enumerable.Range(0, members.Length).Where(i => bits[i]).Select(i => i == unregisteredPosition ? unregisteredKey : members[i])];
        SyncAggregate aggregate = new() { SyncCommitteeBits = bits, SyncCommitteeSignature = AggregateSignature(fixture.SigningRoot, honest ? signers : signers[1..]) };
        Action process = () => Process(batch => fixture.Process(aggregate, verifySignature, batch), batched);

        if (verifySignature && !honest)
            Assert.That(process, Throws.TypeOf<BeaconStateException>().With.Message.EqualTo("Invalid sync aggregate signature"));
        else
            Assert.That(process, Throws.TypeOf<BeaconStateException>().With.Message.Contains("is not a registered validator"));
    }

    [Test]
    public void Sync_committee_bits_of_the_wrong_width_fail_the_block([Values(0, 511, 513)] int width, [Values] bool gloas, [Values] bool verifySignature)
    {
        SyncFixture fixture = CreateSyncFixture(gloas, RepeatingCommittee());
        SyncAggregate aggregate = new() { SyncCommitteeBits = new BitArray(width, true), SyncCommitteeSignature = new BlsSignature(G2PointAtInfinity()) };
        Action process = () => fixture.Process(aggregate, verifySignature, null);

        Assert.That(process, Throws.TypeOf<BeaconStateException>().With.Message.EqualTo($"Sync committee bits have {width} entries, expected {Presets.SyncCommitteeSize}"));
    }

    [Test]
    public void A_sync_aggregate_of_the_wrong_length_does_not_decode([Values(0, 159, 161)] int length)
    {
        byte[] encoded = new byte[length];

        Assert.That(() => SyncAggregate.Decode(encoded, out SyncAggregate _), Throws.TypeOf<InvalidDataException>().With.Message.Contains("expected 160 bytes"));
    }

    private sealed record SyncFixture(
        Validator[] Validators, SyncCommittee Committee, PubkeyCache Pubkeys, Hash256 SigningRoot,
        Action<SyncAggregate, bool, BlockSignatureBatch?> Process);

    private static SyncFixture CreateSyncFixture(bool gloas, int[] members, int keyedValidators = 7, int cachedValidators = RegistrySize)
    {
        if (gloas)
        {
            BeaconStateGloas state = GloasStateWithCommittee(members, keyedValidators, out Hash256 signingRoot);
            PubkeyCache pubkeys = new();
            pubkeys.Build(state.Validators![..keyedValidators]);
            return new SyncFixture(state.Validators, state.CurrentSyncCommittee!, pubkeys, signingRoot,
                (aggregate, verify, batch) => GloasBlockProcessing.ProcessSyncAggregate(state, aggregate, new EpochCache(), pubkeys, verify, batch));
        }

        BeaconStateFulu fulu = CreateState(members, out Hash256 root);
        PubkeyCache cache = CacheOf(fulu, cachedValidators);
        return new SyncFixture(fulu.Validators!, fulu.CurrentSyncCommittee!, cache, root,
            (aggregate, verify, batch) => BlockProcessing.ProcessSyncAggregate(fulu, aggregate, new EpochCache(), cache, verify, batch));
    }

    private static BlsSignature HonestSignature(Hash256 signingRoot, int[] members, BitArray bits) =>
        AggregateSignature(signingRoot, [.. Enumerable.Range(0, members.Length).Where(i => bits[i]).Select(i => members[i])]);

    private static BlsSignature NoParticipantSignature(bool infinity, Hash256 signingRoot) =>
        infinity ? new BlsSignature(G2PointAtInfinity()) : Sign(ValidatorKey(0), signingRoot);

    private static BeaconStateGloas GloasStateWithCommittee(int[] members, int keyedValidators, out Hash256 signingRoot)
    {
        BeaconStateGloas state = CreateGloasState(out _, out _);
        Validator[] validators = state.Validators!;
        for (int i = 0; i < keyedValidators; i++)
        {
            Validator updated = validators[i].Clone();
            updated.Pubkey = new BlsPublicKey(new Bls.P1(ValidatorKey(i)).Compress());
            validators[i] = updated;
        }

        state.CurrentSyncCommittee = new SyncCommittee { Pubkeys = [.. members.Select(m => validators[m].Pubkey)], AggregatePubkey = Pubkey(0x60) };
        ulong previousSlot = state.Slot - 1;
        Hash256 domain = state.GetDomain(DomainType.SyncCommittee, BeaconStateAccessors.ComputeEpochAtSlot(previousSlot));
        signingRoot = Domains.ComputeSigningRoot(state.GetBlockRootAtSlot(previousSlot), domain);
        return state;
    }

    private static BitArray LastMemberBits(bool lastParticipates) =>
        Bits(i => i == Presets.SyncCommitteeSize - 1 ? lastParticipates : i % 3 != 1);

    private static BlsSignature HonestSignatureWithoutLastMember(Hash256 signingRoot, int[] members, BitArray bits) =>
        AggregateSignature(signingRoot, [.. Enumerable.Range(0, members.Length - 1).Where(i => bits[i]).Select(i => members[i])]);

    private static void SetPubkey(Validator[] validators, SyncCommittee committee, int[] members, int validatorIndex, BlsPublicKey pubkey)
    {
        Validator updated = validators[validatorIndex].Clone();
        updated.Pubkey = pubkey;
        validators[validatorIndex] = updated;
        for (int i = 0; i < members.Length; i++)
        {
            if (members[i] == validatorIndex)
                committee.Pubkeys![i] = pubkey;
        }
    }

    private static (bool Serial, bool Batched) Verdicts(BeaconStateFulu state, PubkeyCache pubkeys, BitArray bits, BlsSignature signature)
    {
        SyncAggregate syncAggregate = new() { SyncCommitteeBits = bits, SyncCommitteeSignature = signature };
        int[] indices = new EpochCache().GetSyncCommitteeIndices(state.CurrentSyncCommittee!, state.Validators!);

        return SerialAndBatched(deferral => SignatureSets.VerifySyncAggregate(state, syncAggregate, indices, pubkeys, deferral));
    }

    private static (bool Serial, bool Batched) SerialAndBatched(Func<BlockSignatureBatch.Deferral?, bool> verify)
    {
        bool serial = verify(null);

        BlockSignatureBatch batch = new();
        bool batched = verify(batch.Defer("deferred"));
        try
        {
            batch.Verify();
        }
        catch (BeaconStateException)
        {
            batched = false;
        }

        return (serial, batched);
    }

    private static void Process(Action<BlockSignatureBatch?> process, bool batched)
    {
        if (batched)
            BlockSignatureBatch.Run(batch => process(batch));
        else
            process(null);
    }

    private static void AssertParticipantsSumToInfinity(BitArray bits, SyncCommittee committee, Validator[] validators, PubkeyCache pubkeys)
    {
        Bls.P1 aggregate = new(new long[Bls.P1.Sz]);
        SignatureSets.AggregateSyncParticipants(bits, committee.Pubkeys!, new EpochCache().GetSyncCommitteeIndices(committee, validators), pubkeys, aggregate);
        Assert.That(aggregate.IsInf(), Is.True, "fixture bug: the participants must sum to infinity");
    }

    private static BlsSignature InfinityOrSignedBy(bool infinity, int validatorIndex, Hash256 signingRoot) =>
        infinity ? new BlsSignature(G2PointAtInfinity()) : Sign(ValidatorKey(validatorIndex), signingRoot);

    private static BlsPublicKey NegatedKey(int validatorIndex)
    {
        Bls.P1 point = new(ValidatorKey(validatorIndex));
        point.Neg();
        return new BlsPublicKey(point.Compress());
    }

    private static BitArray Bits(Func<int, bool> participates)
    {
        BitArray bits = new(Presets.SyncCommitteeSize);
        for (int i = 0; i < bits.Length; i++)
            bits[i] = participates(i);
        return bits;
    }

    private static PubkeyCache CacheOf(BeaconStateFulu state, int count)
    {
        Validator[] validators = state.Validators!;
        PubkeyCache pubkeys = new();
        pubkeys.Build(count <= validators.Length
            ? validators[..count]
            : [.. validators, .. Enumerable.Range(0, count - validators.Length).Select(static i => new Validator { Pubkey = new BlsPublicKey(new Bls.P1(ValidatorKey(100 + i)).Compress()) })]);
        return pubkeys;
    }

    private static BeaconStateFulu CreateState(int[] members, out Hash256 signingRoot)
    {
        BeaconStateFulu state = CreateFuluState(RegistrySize + 2);
        Validator[] validators = state.Validators!;
        for (int i = 0; i < validators.Length; i++)
        {
            Validator updated = validators[i].Clone();
            updated.Pubkey = i == NegatedKeyIndex ? NegatedKey(KeyIndex) : new BlsPublicKey(new Bls.P1(ValidatorKey(i)).Compress());
            validators[i] = updated;
        }

        state.Slot = 1;
        state.CurrentSyncCommittee = new SyncCommittee { Pubkeys = [.. members.Select(m => validators[m].Pubkey)], AggregatePubkey = Pubkey(0x60) };
        signingRoot = Domains.ComputeSigningRoot(state.GetBlockRootAtSlot(0), state.GetDomain(DomainType.SyncCommittee, 0));
        return state;
    }
}
