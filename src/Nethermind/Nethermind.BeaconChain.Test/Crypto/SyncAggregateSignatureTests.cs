// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.IO;
using System.Linq;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.StateTransition.GloasTestFixtures;

namespace Nethermind.BeaconChain.Test.Crypto;

/// <summary>
/// The sync aggregate check over cached public keys must give the verdict <c>eth_fast_aggregate_verify</c>
/// gives over the committee's compressed participant pubkeys, repeated members included.
/// </summary>
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

    /// <summary>No cache, a cache short of the committee, the whole registry, and a cache past the registry.</summary>
    private static readonly int[] CacheSizes = [0, 4, RegistrySize + 2, RegistrySize + 12];

    /// <summary>Committee position i seats validator i % 7, so every member repeats.</summary>
    private static int[] RepeatingCommittee() => [.. Enumerable.Range(0, Presets.SyncCommitteeSize).Select(static i => i % 7)];

    /// <summary>Position i participates when i is a multiple of <paramref name="stride"/>: all, a third, or position 0 alone.</summary>
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

    /// <summary><c>eth_fast_aggregate_verify</c>: with no participants only the G2 point at infinity is valid.</summary>
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

    /// <summary>
    /// <c>FastAggregateVerify</c> is <c>CoreVerify</c> over the aggregate key, whose <c>KeyValidate</c> refuses infinity, so
    /// participants whose keys sum to infinity fail the block with any signature, the infinity signature included.
    /// </summary>
    [Test]
    public void Participants_whose_keys_sum_to_infinity_fail_the_block([Values] bool gloas, [Values] bool batched, [Values] bool infinitySignature)
    {
        BitArray bits = Bits(static i => i < 2);
        Action<BlockSignatureBatch?> process;
        if (gloas)
        {
            int[] members = [.. Enumerable.Range(0, Presets.SyncCommitteeSize).Select(static i => i % 2)];
            BeaconStateGloas state = GloasStateWithCommittee(members, 2, out Hash256 signingRoot);
            SetPubkey(state.Validators!, state.CurrentSyncCommittee!, members, 1, NegatedKey(0));
            PubkeyCache pubkeys = new();
            pubkeys.Build(state.Validators![..2]);
            AssertParticipantsSumToInfinity(bits, state.CurrentSyncCommittee!, state.Validators!, pubkeys);
            SyncAggregate syncAggregate = new() { SyncCommitteeBits = bits, SyncCommitteeSignature = InfinityOrSignedBy(infinitySignature, 0, signingRoot) };
            process = batch => GloasBlockProcessing.ProcessSyncAggregate(state, syncAggregate, new EpochCache(), pubkeys, verifySignature: true, batch);
        }
        else
        {
            int[] members = [.. Enumerable.Range(0, Presets.SyncCommitteeSize).Select(static i => i % 2 == 0 ? KeyIndex : NegatedKeyIndex)];
            BeaconStateFulu state = CreateState(members, out Hash256 signingRoot);
            PubkeyCache pubkeys = CacheOf(state, state.Validators!.Length);
            AssertParticipantsSumToInfinity(bits, state.CurrentSyncCommittee!, state.Validators!, pubkeys);
            SyncAggregate syncAggregate = new() { SyncCommitteeBits = bits, SyncCommitteeSignature = InfinityOrSignedBy(infinitySignature, KeyIndex, signingRoot) };
            process = batch => BlockProcessing.ProcessSyncAggregate(state, syncAggregate, new EpochCache(), pubkeys, verifySignature: true, batch);
        }

        Assert.That(() => Process(process, batched), Throws.TypeOf<BeaconStateException>().With.Message.EqualTo("Invalid sync aggregate signature"));
    }

    public enum AttestationKind
    {
        Fulu,
        Gloas,
        GloasPayload,
    }

    /// <summary>
    /// The attestation twin of the rule above: <c>is_valid_indexed_attestation</c> and
    /// <c>is_valid_indexed_payload_attestation</c> call the same <c>FastAggregateVerify</c>.
    /// </summary>
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

    /// <summary>
    /// <c>FastAggregateVerify</c> runs <c>KeyValidate</c> on each participant, so a member at infinity refuses the
    /// aggregate even though it adds nothing and the others signed honestly. The cache refuses to hold that key, so the
    /// member is always decompressed.
    /// </summary>
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

    /// <summary>A cached key that differs from the committee's stored key is not used: the verdict follows the state.</summary>
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

    /// <summary>
    /// A participant whose key does not decode fails <c>eth_fast_aggregate_verify</c> even when the others signed
    /// honestly; dropping that member instead would accept the others' aggregate.
    /// </summary>
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

    /// <summary>
    /// A registry key off the G1 subgroup pairs like its subgroup part, so every member signing honestly still satisfies
    /// the pairing; <c>KeyValidate</c> on each member key must refuse it, whether the key is cached or decoded.
    /// </summary>
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

    /// <summary>
    /// <c>FastAggregateVerify</c> runs <c>KeyValidate</c> on each member, so two members outside G1 whose torsion cancels
    /// refuse the aggregate even though their sum is in G1 and they signed honestly, whether their keys are cached or decoded.
    /// </summary>
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
        BeaconStateGloas state = GloasStateWithCommittee(members, undecodableIndex, out Hash256 signingRoot);
        Validator[] validators = state.Validators!;
        SetPubkey(validators, state.CurrentSyncCommittee!, members, undecodableIndex, UndecodableKey);
        PubkeyCache pubkeys = new();
        pubkeys.Build(validators[..undecodableIndex]);

        BitArray bits = LastMemberBits(undecodableParticipates);
        SyncAggregate syncAggregate = new() { SyncCommitteeBits = bits, SyncCommitteeSignature = HonestSignatureWithoutLastMember(signingRoot, members, bits) };

        Action process = () => GloasBlockProcessing.ProcessSyncAggregate(state, syncAggregate, new EpochCache(), pubkeys);

        if (undecodableParticipates)
            Assert.That(process, Throws.TypeOf<BeaconStateException>().With.Message.EqualTo("Invalid sync aggregate signature"));
        else
            Assert.That(process, Throws.Nothing);
    }

    /// <summary><c>process_sync_aggregate</c> with no participants: only the G2 point at infinity passes, before and after Gloas.</summary>
    [Test]
    public void Processing_no_participants_accepts_only_the_infinity_signature([Values] bool gloas, [Values] bool infinitySignature)
    {
        const int keyedValidators = 7;
        int[] members = RepeatingCommittee();
        BitArray bits = Bits(static _ => false);
        Action process;
        if (gloas)
        {
            BeaconStateGloas state = GloasStateWithCommittee(members, keyedValidators, out Hash256 signingRoot);
            SyncAggregate syncAggregate = new() { SyncCommitteeBits = bits, SyncCommitteeSignature = NoParticipantSignature(infinitySignature, signingRoot) };
            PubkeyCache pubkeys = new();
            pubkeys.Build(state.Validators![..keyedValidators]);
            process = () => GloasBlockProcessing.ProcessSyncAggregate(state, syncAggregate, new EpochCache(), pubkeys);
        }
        else
        {
            BeaconStateFulu state = CreateState(members, out Hash256 signingRoot);
            SyncAggregate syncAggregate = new() { SyncCommitteeBits = bits, SyncCommitteeSignature = NoParticipantSignature(infinitySignature, signingRoot) };
            PubkeyCache pubkeys = CacheOf(state, RegistrySize);
            process = () => BlockProcessing.ProcessSyncAggregate(state, syncAggregate, new EpochCache(), pubkeys);
        }

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

    /// <summary>
    /// <c>process_sync_aggregate</c> with participants refuses every signature but the honest one, serially and batched;
    /// a signature outside G2 is refused even when it is the honest signature plus a torsion point.
    /// </summary>
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

    /// <summary>A valid aggregate is deferred to the batch, not verified at once, before and after Gloas.</summary>
    [Test]
    public void A_valid_aggregate_is_deferred_to_the_batch([Values] bool gloas)
    {
        const int keyedValidators = 7;
        int[] members = RepeatingCommittee();
        BitArray bits = Bits(static i => i % 3 != 1);
        BlockSignatureBatch batch = new();
        if (gloas)
        {
            BeaconStateGloas state = GloasStateWithCommittee(members, keyedValidators, out Hash256 signingRoot);
            PubkeyCache pubkeys = new();
            pubkeys.Build(state.Validators![..keyedValidators]);
            SyncAggregate syncAggregate = new() { SyncCommitteeBits = bits, SyncCommitteeSignature = HonestSignature(signingRoot, members, bits) };
            GloasBlockProcessing.ProcessSyncAggregate(state, syncAggregate, new EpochCache(), pubkeys, verifySignature: true, batch);
        }
        else
        {
            BeaconStateFulu state = CreateState(members, out Hash256 signingRoot);
            SyncAggregate syncAggregate = new() { SyncCommitteeBits = bits, SyncCommitteeSignature = HonestSignature(signingRoot, members, bits) };
            BlockProcessing.ProcessSyncAggregate(state, syncAggregate, new EpochCache(), CacheOf(state, RegistrySize), verifySignature: true, batch);
        }

        Assert.That(batch.Count, Is.EqualTo(1));
        Assert.That(batch.Verify, Throws.Nothing);
    }

    /// <summary>
    /// <c>process_sync_aggregate</c> checks the signature first, then looks every committee member up in the registry:
    /// a member missing from it fails the block, but a checked invalid signature is the reported failure.
    /// </summary>
    [Test]
    public void A_committee_member_missing_from_the_registry_fails_the_block_after_the_signature_check([Values] bool gloas, [Values] bool verifySignature, [Values] bool batched, [Values] bool participates, [Values] bool honest)
    {
        const int unregisteredPosition = 3;
        const int unregisteredKey = 200;
        const int keyedValidators = 7;
        int[] members = RepeatingCommittee();
        BlsPublicKey unregistered = new(new Bls.P1(ValidatorKey(unregisteredKey)).Compress());
        BitArray bits = Bits(i => i != unregisteredPosition || participates);
        int[] signers = [.. Enumerable.Range(0, members.Length).Where(i => bits[i]).Select(i => i == unregisteredPosition ? unregisteredKey : members[i])];
        SyncAggregate syncAggregate = new() { SyncCommitteeBits = bits };

        Action<BlockSignatureBatch?> processBlock;
        if (gloas)
        {
            BeaconStateGloas state = GloasStateWithCommittee(members, keyedValidators, out Hash256 signingRoot);
            state.CurrentSyncCommittee!.Pubkeys![unregisteredPosition] = unregistered;
            syncAggregate.SyncCommitteeSignature = AggregateSignature(signingRoot, honest ? signers : signers[1..]);
            PubkeyCache pubkeys = new();
            pubkeys.Build(state.Validators![..keyedValidators]);
            processBlock = batch => GloasBlockProcessing.ProcessSyncAggregate(state, syncAggregate, new EpochCache(), pubkeys, verifySignature, batch);
        }
        else
        {
            BeaconStateFulu state = CreateState(members, out Hash256 signingRoot);
            state.CurrentSyncCommittee!.Pubkeys![unregisteredPosition] = unregistered;
            syncAggregate.SyncCommitteeSignature = AggregateSignature(signingRoot, honest ? signers : signers[1..]);
            PubkeyCache pubkeys = CacheOf(state, RegistrySize);
            processBlock = batch => BlockProcessing.ProcessSyncAggregate(state, syncAggregate, new EpochCache(), pubkeys, verifySignature, batch);
        }

        Action process = () => Process(processBlock, batched);

        if (verifySignature && !honest)
            Assert.That(process, Throws.TypeOf<BeaconStateException>().With.Message.EqualTo("Invalid sync aggregate signature"));
        else
            Assert.That(process, Throws.TypeOf<BeaconStateException>().With.Message.Contains("is not a registered validator"));
    }

    /// <summary>Sync committee bits are a <c>Bitvector[SYNC_COMMITTEE_SIZE]</c>; any other width fails the block, verified or not.</summary>
    [Test]
    public void Sync_committee_bits_of_the_wrong_width_fail_the_block([Values(0, 511, 513)] int width, [Values] bool gloas, [Values] bool verifySignature)
    {
        SyncAggregate syncAggregate = new() { SyncCommitteeBits = new BitArray(width, true), SyncCommitteeSignature = new BlsSignature(G2PointAtInfinity()) };
        Action process;
        if (gloas)
        {
            BeaconStateGloas state = GloasStateWithCommittee(RepeatingCommittee(), 7, out _);
            PubkeyCache pubkeys = new();
            pubkeys.Build(state.Validators![..7]);
            process = () => GloasBlockProcessing.ProcessSyncAggregate(state, syncAggregate, new EpochCache(), pubkeys, verifySignature);
        }
        else
        {
            BeaconStateFulu state = CreateState(RepeatingCommittee(), out _);
            process = () => BlockProcessing.ProcessSyncAggregate(state, syncAggregate, new EpochCache(), CacheOf(state, RegistrySize), verifySignature);
        }

        Assert.That(process, Throws.TypeOf<BeaconStateException>().With.Message.EqualTo($"Sync committee bits have {width} entries, expected {Presets.SyncCommitteeSize}"));
    }

    /// <summary>A <c>SyncAggregate</c> is a fixed 160-byte container; any other length does not decode.</summary>
    [TestCase(0)]
    [TestCase(159)]
    [TestCase(161)]
    public void A_sync_aggregate_of_the_wrong_length_does_not_decode(int length)
    {
        byte[] encoded = new byte[length];

        Assert.That(() => SyncAggregate.Decode(encoded, out SyncAggregate _), Throws.TypeOf<InvalidDataException>().With.Message.Contains("expected 160 bytes"));
    }

    private static BlsSignature HonestSignature(Hash256 signingRoot, int[] members, BitArray bits) =>
        AggregateSignature(signingRoot, [.. Enumerable.Range(0, members.Length).Where(i => bits[i]).Select(i => members[i])]);

    private static byte[] G1PointAtInfinity()
    {
        byte[] bytes = new byte[BlsPublicKey.Length];
        bytes[0] = 0xc0;
        return bytes;
    }

    private static BlsSignature NoParticipantSignature(bool infinity, Hash256 signingRoot) =>
        infinity ? new BlsSignature(G2PointAtInfinity()) : Sign(ValidatorKey(0), signingRoot);

    /// <summary>
    /// The Gloas fixture state with real keys for validators below <paramref name="keyedValidators"/> and sync committee
    /// position i seating validator <paramref name="members"/>[i].
    /// </summary>
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

    /// <summary>Two thirds of the committee participate; the last position participates when <paramref name="lastParticipates"/>.</summary>
    private static BitArray LastMemberBits(bool lastParticipates) =>
        Bits(i => i == Presets.SyncCommitteeSize - 1 ? lastParticipates : i % 3 != 1);

    /// <summary>The participants' aggregate signature, leaving out the last position, which holds the undecodable key.</summary>
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

    /// <summary>The verdict of <paramref name="verify"/> run at once, and deferred to a batch that is then verified.</summary>
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

    /// <summary>Runs <paramref name="process"/> serially, or through <see cref="BlockSignatureBatch.Run"/>, which reports a failed deferred signature ahead of a later failure.</summary>
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

    /// <summary>The negation of validator <paramref name="validatorIndex"/>'s public key, which sums with it to infinity.</summary>
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

    /// <summary>
    /// A cache of the first <paramref name="count"/> validators, members past it taking the decompressing path; a count
    /// past the registry caches extra keys after it.
    /// </summary>
    private static PubkeyCache CacheOf(BeaconStateFulu state, int count)
    {
        Validator[] validators = state.Validators!;
        PubkeyCache pubkeys = new();
        pubkeys.Build(count <= validators.Length
            ? validators[..count]
            : [.. validators, .. Enumerable.Range(0, count - validators.Length).Select(static i => new Validator { Pubkey = new BlsPublicKey(new Bls.P1(ValidatorKey(100 + i)).Compress()) })]);
        return pubkeys;
    }

    /// <summary>
    /// A state at slot 1 whose registry holds <see cref="RegistrySize"/> real keys plus a key and its negation,
    /// with sync committee position i seating validator <paramref name="members"/>[i].
    /// </summary>
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
