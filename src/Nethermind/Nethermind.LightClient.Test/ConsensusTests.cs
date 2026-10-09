// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Security.Cryptography;
using System.Text.Json;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.LightClient.Consensus;

namespace Nethermind.LightClient.Test;

[TestFixture]
public class ConsensusTests
{
    internal static readonly BeaconChainSpec Spec = BeaconChainSpec.Mainnet with
    {
        ElectraForkEpoch = 0,
        FuluForkEpoch = 1,
        GloasForkEpoch = 1000,
        Forks = [new([0, 0, 0, 0], 0), new([1, 0, 0, 0], 1)],
    };

    [Test]
    public void Authenticates_finalized_execution_state_and_detaches_snapshots()
    {
        LightClientBootstrap bootstrap = Bootstrap(1, 1);
        LightClientStore store = Store(bootstrap);
        LightClientUpdate update = Update(2, 3, 4, 1);
        store.Process(update, 4);
        Hash256 expectedRoot = update.FinalizedHeader!.Execution!.StateRoot!;
        update.FinalizedHeader.Execution.StateRoot = Hash256.Zero;
        LightClientHeader copy = store.FinalizedHeader;
        copy.Execution!.StateRoot = Hash256.Zero;

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(store.FinalizedHeader.Beacon!.Slot, Is.EqualTo(2));
        Assert.That(store.FinalizedHeader.Execution!.StateRoot, Is.EqualTo(expectedRoot));
        Assert.That(store.Period, Is.Zero);
    }

    [Test]
    public void Only_supermajority_advances_finalized_state([Values(341, 342, 343)] int participants)
    {
        LightClientStore store = Store(Bootstrap(1, 1));
        LightClientUpdate update = Update(2, 3, 4, 1, participants: participants);

        store.Process(update, 4);
        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(store.FinalizedHeader.Beacon!.Slot, Is.EqualTo(participants == 341 ? 1 : 2));
        Assert.That(store.OptimisticHeader.Beacon!.Slot, Is.EqualTo(3));
    }

    [Test]
    public void Optimistic_update_never_changes_finalized_state([Values(0, 1, 256, 342)] int participants)
    {
        LightClientStore store = Store(Bootstrap(1, 1));
        LightClientUpdate signed = Update(2, 3, 4, 1, participants: participants);
        LightClientOptimisticUpdate update = new()
        {
            AttestedHeader = signed.AttestedHeader,
            SyncAggregate = signed.SyncAggregate,
            SignatureSlot = signed.SignatureSlot,
        };

        if (participants == 0)
            Assert.That(() => store.Process(update, 4), Throws.TypeOf<InvalidDataException>().With.Message.Contains("participants"));
        else store.Process(update, 4);

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(store.FinalizedHeader.Beacon!.Slot, Is.EqualTo(1));
        Assert.That(store.OptimisticHeader.Beacon!.Slot, Is.EqualTo(participants == 0 ? 1 : 3));
    }

    [Test]
    public void New_finality_keeps_a_higher_optimistic_head_per_sync_protocol()
    {
        LightClientStore store = Store(Bootstrap(1, 1));
        LightClientUpdate signed = Update(2, 10, 11, 1);
        store.Process(new LightClientOptimisticUpdate
        {
            AttestedHeader = signed.AttestedHeader,
            SyncAggregate = signed.SyncAggregate,
            SignatureSlot = signed.SignatureSlot,
        }, 11);
        Assert.That(store.OptimisticHeader.Beacon!.Slot, Is.EqualTo(10));

        store.Process(Update(2, 3, 4, 1), 11);

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(store.FinalizedHeader.Beacon!.Slot, Is.EqualTo(2));
        Assert.That(store.OptimisticHeader.Beacon!.Slot, Is.EqualTo(10));
    }

    [Test]
    public void Forced_timeout_progress_keeps_finalized_state_separate([Values(8192, 8193)] int elapsedSlots)
    {
        LightClientStore store = Store(Bootstrap(1, 1));
        store.Process(Update(2, 3, 4, 1, participants: 341), 4);

        bool advanced = store.ForceUpdate(1UL + (ulong)elapsedSlots);

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(advanced, Is.EqualTo(elapsedSlots > 8192));
        Assert.That(store.FinalizedHeader.Beacon!.Slot, Is.EqualTo(1));
        Assert.That(store.OptimisticHeader.Beacon!.Slot, Is.EqualTo(3));
    }

    [Test]
    public void Recovery_candidate_is_detached_from_callers()
    {
        LightClientStore store = Store(Bootstrap(1, 1));
        store.Process(Update(2, 3, 4, 1, participants: 341), 4);
        LightClientUpdate candidate = store.BestUpdate!;
        Hash256 original = new(candidate.FinalityBranch![0].Bytes);
        candidate.FinalityBranch[0].Bytes[0] ^= 0xff;
        candidate.AttestedHeader!.Beacon!.Slot = 9000;

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(store.BestUpdate!.FinalityBranch![0], Is.EqualTo(original));
        Assert.That(store.BestUpdate!.AttestedHeader!.Beacon!.Slot, Is.EqualTo(3));
    }

    public enum InvalidUpdate
    {
        ExecutionProof,
        FinalityProof,
        Signature,
        FutureSignature,
        AttestationAtSignatureSlot,
        FinalityAfterAttestation,
        UnknownNextPeriod,
        WrongGenesisRoot,
        UnprovedCommittee,
        NextCommitteeProof,
        BranchLength,
        BitfieldLength,
    }

    [Test]
    public void Invalid_update_preserves_the_authenticated_snapshot([Values] InvalidUpdate invalid)
    {
        LightClientStore store = Store(Bootstrap(1, 1));
        LightClientUpdate update = Update(2, 3, 4, 1);
        switch (invalid)
        {
            case InvalidUpdate.ExecutionProof: update.FinalizedHeader!.ExecutionBranch![0] = Hash(0x42); break;
            case InvalidUpdate.FinalityProof: update.FinalityBranch![0] = Hash(0x42); break;
            case InvalidUpdate.Signature: update.SyncAggregate!.SyncCommitteeSignature = default; break;
            case InvalidUpdate.FutureSignature: update.SignatureSlot = 5; break;
            case InvalidUpdate.AttestationAtSignatureSlot: update.SignatureSlot = 3; break;
            case InvalidUpdate.FinalityAfterAttestation: update.FinalizedHeader!.Beacon!.Slot = 4; break;
            case InvalidUpdate.UnknownNextPeriod: update.SignatureSlot = 8192; break;
            case InvalidUpdate.WrongGenesisRoot: Sign(update, 1, 342, Spec with { GenesisValidatorsRoot = Hash256.Zero }); break;
            case InvalidUpdate.UnprovedCommittee: update.NextSyncCommittee = Committee(2); break;
            case InvalidUpdate.NextCommitteeProof:
                update = Update(2, 3, 4, 1, nextKey: 2);
                update.NextSyncCommitteeBranch![0] = Hash(0x42);
                break;
            case InvalidUpdate.BranchLength: update.FinalityBranch = update.FinalityBranch![..6]; break;
            case InvalidUpdate.BitfieldLength: update.SyncAggregate!.SyncCommitteeBits = new BitArray(511); break;
        }

        if (invalid is InvalidUpdate.FutureSignature or InvalidUpdate.UnknownNextPeriod)
            Assert.That(() => store.Process(update, invalid == InvalidUpdate.UnknownNextPeriod ? 8192UL : 4UL), Throws.TypeOf<LightClientLocalStateException>());
        else
            Assert.That(() => store.Process(update, 4), Throws.TypeOf<InvalidDataException>());
        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(store.FinalizedHeader.Beacon!.Slot, Is.EqualTo(1));
        Assert.That(store.NextSyncCommitteeKnown, Is.False);
    }

    [Test]
    public void Signature_domain_uses_the_slot_before_the_signature_at_a_fork_boundary([Values] bool wrongDomain)
    {
        LightClientStore store = Store(Bootstrap(1, 1));
        LightClientUpdate update = Update(30, 31, 32, 1);
        if (wrongDomain) Sign(update, 1, 342, Spec, domainSlot: 32);

        if (wrongDomain)
            Assert.That(() => store.Process(update, 32), Throws.TypeOf<InvalidDataException>().With.Message.Contains("signature"));
        else
        {
            store.Process(update, 32);
            Assert.That(store.FinalizedHeader.Beacon!.Slot, Is.EqualTo(30));
        }
    }

    [Test]
    public void Learns_then_rotates_committees_and_rejects_the_previous_committee()
    {
        LightClientStore store = Store(Bootstrap(8190, 1));
        store.Process(Update(8190, 8190, 8191, 1, nextKey: 2), 8191);
        Assert.That(store.NextSyncCommitteeKnown, Is.True);
        store.Process(Update(8192, 8193, 8194, 2, nextKey: 3), 8194);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.Period, Is.EqualTo(1));
            Assert.That(store.FinalizedHeader.Beacon!.Slot, Is.EqualTo(8192));
            Assert.That(store.NextSyncCommitteeKnown, Is.True);
        }

        LightClientUpdate forged = Update(8194, 8195, 8196, 1);
        Assert.That(() => store.Process(forged, 8196), Throws.TypeOf<InvalidDataException>().With.Message.Contains("signature"));
        Assert.That(store.FinalizedHeader.Beacon!.Slot, Is.EqualTo(8192));
    }

    [Test]
    public async Task Catch_up_fetches_each_period_and_stops_before_publishing_invalid_data([Values] bool tamper)
    {
        LightClientStore store = Store(Bootstrap(8190, 1));
        List<ulong> requested = [];
        int publications = 0;
        Task CatchUp() => ConsensusSync.CatchUpAsync(store, Spec, (period, validate, _) =>
        {
            requested.Add(period);
            LightClientUpdate update = period switch
            {
                0 => Update(8190, 8190, 8191, 1, nextKey: 2),
                1 => Update(8192, 8193, 8194, 2, nextKey: 3),
                2 => Update(16384, 16385, 16386, 3, nextKey: 4),
                _ => throw new AssertionException("Unexpected sync committee period."),
            };
            if (tamper && period == 1) update.SyncAggregate!.SyncCommitteeSignature = default;
            validate(update);
            return Task.FromResult(update);
        }, () => 16390, (_, _) => Task.CompletedTask, _ =>
        {
            publications++;
            return Task.CompletedTask;
        }, CancellationToken.None);

        if (tamper)
            Assert.That(async () => await CatchUp(), Throws.TypeOf<InvalidDataException>());
        else
            await CatchUp();

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(requested, Is.EqualTo(tamper ? new ulong[] { 0, 1 } : [0, 1, 2]));
        Assert.That(store.Period, Is.EqualTo(tamper ? 0 : 2));
        Assert.That(store.FinalizedHeader.Beacon!.Slot, Is.EqualTo(tamper ? 8190 : 16384));
        Assert.That(publications, Is.EqualTo(tamper ? 1 : 3));
    }

    [Test]
    public void Finality_only_update_keeps_the_known_next_committee()
    {
        LightClientStore store = Store(Bootstrap(1, 1));
        store.Process(Update(2, 3, 4, 1, nextKey: 2), 4);
        LightClientUpdate update = Update(4, 5, 6, 1);
        store.Process(new LightClientFinalityUpdate
        {
            AttestedHeader = update.AttestedHeader,
            FinalizedHeader = update.FinalizedHeader,
            FinalityBranch = update.FinalityBranch,
            SyncAggregate = update.SyncAggregate,
            SignatureSlot = update.SignatureSlot,
        }, 6);

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(store.FinalizedHeader.Beacon!.Slot, Is.EqualTo(4));
        Assert.That(store.NextSyncCommitteeKnown, Is.True);
    }

    [Test]
    public void Refuses_a_malformed_or_infinity_participant_key([Values] bool infinity)
    {
        LightClientBootstrap bootstrap = Bootstrap(1, 1);
        byte[] encoding = new byte[48];
        if (infinity) encoding[0] = 0xc0;
        bootstrap.CurrentSyncCommittee!.Pubkeys![0] = new BlsPublicKey(encoding);
        Hash256[] tree = Tree(new Dictionary<int, Hash256> { [86] = SszRoots.HashTreeRoot(bootstrap.CurrentSyncCommittee) });
        bootstrap.Header!.Beacon!.StateRoot = tree[1];
        bootstrap.CurrentSyncCommitteeBranch = Branch(tree, 86);
        LightClientStore store = Store(bootstrap);

        Assert.That(() => store.Process(Update(2, 3, 4, 1), 4), Throws.TypeOf<InvalidDataException>().With.Message.Contains("public key"));
    }

    [Test]
    public void Checkpoint_age_policy_accepts_its_boundary_and_refuses_the_next_slot([Values(100799, 100800, 100801)] int ageSlots)
    {
        LightClientBootstrap bootstrap = Bootstrap(1, 1);
        Hash256 checkpoint = SszRoots.HashTreeRoot(bootstrap.Header!.Beacon!);
        BeaconChainSpec spec = Spec with { GloasForkEpoch = ulong.MaxValue };

        if (ageSlots > 100800)
            Assert.That(() => new LightClientStore(spec, checkpoint, bootstrap, (ulong)ageSlots + 1), Throws.TypeOf<LightClientLocalStateException>().With.Message.Contains("fourteen days"));
        else
            Assert.That(new LightClientStore(spec, checkpoint, bootstrap, (ulong)ageSlots + 1).FinalizedHeader.Beacon!.Slot, Is.EqualTo(1));
    }

    [Test]
    public void Refuses_unknown_forks([Values] bool beforeElectra)
    {
        LightClientBootstrap bootstrap = Bootstrap(1, 1);
        BeaconChainSpec unsupported = beforeElectra ? Spec with { ElectraForkEpoch = 1 } : Spec with { GloasForkEpoch = 0 };
        if (beforeElectra)
            Assert.That(() => new LightClientStore(unsupported, SszRoots.HashTreeRoot(bootstrap.Header!.Beacon!), bootstrap, 1), Throws.TypeOf<LightClientLocalStateException>());
        else
            Assert.That(() => new LightClientStore(unsupported, SszRoots.HashTreeRoot(bootstrap.Header!.Beacon!), bootstrap, 1), Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public void Refuses_wrong_checkpoint_future_bootstrap_and_expired_checkpoint([Values(0, 1, 2)] int invalid)
    {
        LightClientBootstrap bootstrap = Bootstrap(1, 1);
        Hash256 checkpoint = invalid == 0 ? Hash(0x42) : SszRoots.HashTreeRoot(bootstrap.Header!.Beacon!);
        ulong currentSlot = invalid == 1 ? 0 : invalid == 2 ? 100802UL : 1;
        if (invalid == 0)
            Assert.That(() => new LightClientStore(Spec with { GloasForkEpoch = ulong.MaxValue }, checkpoint, bootstrap, currentSlot), Throws.TypeOf<InvalidDataException>());
        else
            Assert.That(() => new LightClientStore(Spec with { GloasForkEpoch = ulong.MaxValue }, checkpoint, bootstrap, currentSlot), Throws.TypeOf<LightClientLocalStateException>());
    }

    [Test]
    public void Generated_ssz_containers_round_trip_authenticated_data()
    {
        LightClientBootstrap bootstrap = Bootstrap(1, 1);
        LightClientBootstrap.Decode(LightClientBootstrap.Encode(bootstrap), out LightClientBootstrap decodedBootstrap);
        LightClientStore store = Store(decodedBootstrap);
        LightClientUpdate update = Update(2, 3, 4, 1, nextKey: 2);
        LightClientUpdate.Decode(LightClientUpdate.Encode(update), out LightClientUpdate decodedUpdate);
        store.Process(decodedUpdate, 4);

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(store.FinalizedHeader.Execution!.StateRoot, Is.EqualTo(update.FinalizedHeader!.Execution!.StateRoot));
        Assert.That(store.NextSyncCommitteeKnown, Is.True);
    }

    [Test]
    public void Gloas_wire_data_authenticates_execution_hash_and_finality_across_fork(
        [Values(31UL, 32UL)] ulong finalizedSlot, [Values] bool tamper)
    {
        BeaconChainSpec spec = Spec with { GloasForkEpoch = 1 };
        LightClientBootstrap bootstrap = Bootstrap(finalizedSlot - 1, 1);
        LightClientBootstrap decodedBootstrap = LightClientWireCodec.DecodeBootstrap(LightClientWireCodec.EncodeBootstrap(bootstrap, spec), spec);
        LightClientStore store = new(spec, SszRoots.HashTreeRoot(decodedBootstrap.Header!.Beacon!), decodedBootstrap, 34);
        LightClientUpdate update = GloasUpdate(finalizedSlot, 33, 34, 1, spec);
        LightClientUpdate decoded = LightClientWireCodec.DecodeUpdate(LightClientWireCodec.EncodeUpdate(update, spec), spec);
        if (tamper) decoded.FinalizedHeader!.ExecutionBranch![0] = Hash(0x42);

        if (tamper)
            Assert.That(() => store.Process(decoded, 34), Throws.TypeOf<InvalidDataException>().With.Message.Contains("execution"));
        else store.Process(decoded, 34);

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(store.FinalizedHeader.Beacon!.Slot, Is.EqualTo(tamper ? finalizedSlot - 1 : finalizedSlot));
        Assert.That(store.FinalizedHeader.ExecutionBlockHash, Is.EqualTo(tamper ? bootstrap.Header!.Execution!.BlockHash : update.FinalizedHeader!.ExecutionBlockHash));
        Assert.That(store.FinalizedHeader.IsGloas, Is.EqualTo(!tamper));
    }

    [Test]
    public void Gloas_bootstrap_and_noncanonical_wire_data_are_rejected()
    {
        BeaconChainSpec spec = Spec with { GloasForkEpoch = 1 };
        LightClientBootstrap bootstrap = GloasBootstrap(32, 1);
        byte[] wire = LightClientWireCodec.EncodeBootstrap(bootstrap, spec);
        LightClientBootstrap decoded = LightClientWireCodec.DecodeBootstrap(wire, spec);
        LightClientStore store = new(spec, SszRoots.HashTreeRoot(decoded.Header!.Beacon!), decoded, 32);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.FinalizedHeader.IsGloas, Is.True);
            Assert.That(store.FinalizedHeader.ExecutionBlockHash, Is.EqualTo(bootstrap.Header!.ExecutionBlockHash));
        }
        Assert.That(() => LightClientWireCodec.DecodeBootstrap([.. wire, 0], spec), Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public void Legacy_wire_offsets_cannot_be_mistaken_for_a_gloas_slot()
    {
        BeaconChainSpec spec = Spec with { GloasForkEpoch = 1 };
        LightClientBootstrap bootstrap = Bootstrap(31, 1);
        byte[] wire = LightClientWireCodec.EncodeBootstrap(bootstrap, spec);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(BitConverter.ToUInt64(wire), Is.Not.EqualTo(31UL));
            Assert.That(LightClientWireCodec.DecodeBootstrap(wire, spec).Header!.Beacon!.Slot, Is.EqualTo(31));
        }
        Assert.That(() => LightClientWireCodec.DecodeBootstrap(wire, spec with { GloasForkEpoch = 0 }),
            Throws.TypeOf<InvalidDataException>());
        byte[] invalidOffset = [.. wire];
        invalidOffset.AsSpan(0, sizeof(int)).Clear();
        Assert.That(() => LightClientWireCodec.DecodeBootstrap(invalidOffset, spec),
            Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public void Official_mainnet_electra_proofs_authenticate_the_reference_state([Values("current_sync_committee", "next_sync_committee", "finality_root")] string field)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "TestData", "Electra");
        BeaconStateElectra.Decode(Snappier.Snappy.DecompressToArray(File.ReadAllBytes(Path.Combine(directory, "BeaconState.ssz_snappy"))), out BeaconStateElectra state);
        Hash256 root = SszRoots.HashTreeRoot(state);
        using JsonDocument fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, field + "_merkle_proof.json")));
        JsonElement proof = fixture.RootElement;
        Hash256 leaf = new(proof.GetProperty("leaf").GetString()!);
        Hash256[] branch = proof.GetProperty("branch").EnumerateArray().Select(hash => new Hash256(hash.GetString()!)).ToArray();
        int generalizedIndex = proof.GetProperty("generalizedIndex").GetInt32();

        Assert.That(LightClientStore.VerifyBranch(leaf, branch, generalizedIndex, root), Is.True);
        branch[0] = Hash(0x42);
        Assert.That(LightClientStore.VerifyBranch(leaf, branch, generalizedIndex, root), Is.False);
    }

    private static LightClientStore Store(LightClientBootstrap bootstrap) =>
        new(Spec, SszRoots.HashTreeRoot(bootstrap.Header!.Beacon!), bootstrap, bootstrap.Header.Beacon!.Slot);

    internal static LightClientBootstrap Bootstrap(ulong slot, byte key)
    {
        SyncCommittee committee = Committee(key);
        Hash256[] tree = Tree(new Dictionary<int, Hash256> { [86] = SszRoots.HashTreeRoot(committee) });
        LightClientHeader header = Header(slot);
        header.Beacon!.StateRoot = tree[1];
        return new() { Header = header, CurrentSyncCommittee = committee, CurrentSyncCommitteeBranch = Branch(tree, 86) };
    }

    internal static LightClientUpdate Update(ulong finalizedSlot, ulong attestedSlot, ulong signatureSlot, byte key, int participants = 342, byte? nextKey = null)
    {
        LightClientHeader finalized = Header(finalizedSlot);
        SyncCommittee next = nextKey.HasValue ? Committee(nextKey.Value) : new SyncCommittee { Pubkeys = new BlsPublicKey[512] };
        Dictionary<int, Hash256> leaves = new() { [169] = SszRoots.HashTreeRoot(finalized.Beacon!) };
        if (nextKey.HasValue) leaves[87] = SszRoots.HashTreeRoot(next);
        Hash256[] tree = Tree(leaves);
        LightClientHeader attested = Header(attestedSlot);
        attested.Beacon!.StateRoot = tree[1];
        LightClientUpdate update = new()
        {
            FinalizedHeader = finalized,
            AttestedHeader = attested,
            SignatureSlot = signatureSlot,
            FinalityBranch = Branch(tree, 169),
            NextSyncCommittee = next,
            NextSyncCommitteeBranch = nextKey.HasValue ? Branch(tree, 87) : Enumerable.Repeat(Hash256.Zero, 6).ToArray(),
        };
        Sign(update, key, participants, Spec);
        return update;
    }

    private static void Sign(LightClientUpdate update, byte key, int participants, BeaconChainSpec spec, ulong? domainSlot = null)
    {
        Hash256 domain = Domains.ComputeDomain(DomainType.SyncCommittee, spec.VersionForEpoch(spec.GetEpoch(domainSlot ?? update.SignatureSlot - 1)), spec.GenesisValidatorsRoot);
        Hash256 signingRoot = Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(update.AttestedHeader!.Beacon!), domain);
        BlsSigner.Signature signature = new(new Bls.P2(new long[Bls.P2.Sz]));
        BlsSigner.Signature memberSignature = BlsSigner.Sign(Key(key), signingRoot.Bytes);
        BitArray bits = new(512);
        for (int i = 0; i < participants; i++)
        {
            bits[i] = true;
            signature.Aggregate(memberSignature);
        }
        update.SyncAggregate = new SyncAggregate { SyncCommitteeBits = bits, SyncCommitteeSignature = new BlsSignature(signature.Bytes) };
    }

    private static SyncCommittee Committee(byte key) => new()
    {
        Pubkeys = Enumerable.Repeat(new BlsPublicKey(new Bls.P1(Key(key)).Compress()), 512).ToArray(),
    };

    private static Bls.SecretKey Key(byte key)
    {
        byte[] bytes = new byte[32];
        bytes[0] = key;
        return new Bls.SecretKey(bytes, Bls.ByteOrder.LittleEndian);
    }

    private static LightClientHeader Header(ulong slot)
    {
        ExecutionPayloadHeader execution = new()
        {
            ParentHash = Hash256.Zero,
            FeeRecipient = Address.Zero,
            StateRoot = Hash(0x21),
            ReceiptsRoot = Hash256.Zero,
            LogsBloom = new Bloom(),
            PrevRandao = Hash256.Zero,
            ExtraData = [],
            BlockHash = Hash(0x22),
            TransactionsRoot = Hash256.Zero,
            WithdrawalsRoot = Hash256.Zero,
        };
        Hash256[] tree = Tree(new Dictionary<int, Hash256> { [25] = SszRoots.HashTreeRoot(execution) });
        return new LightClientHeader
        {
            Beacon = new BeaconBlockHeader { Slot = slot, ParentRoot = Hash256.Zero, StateRoot = Hash256.Zero, BodyRoot = tree[1] },
            Execution = execution,
            ExecutionBranch = Branch(tree, 25),
        };
    }

    private static LightClientBootstrap GloasBootstrap(ulong slot, byte key)
    {
        SyncCommittee committee = Committee(key);
        Hash256[] tree = Tree(new Dictionary<int, Hash256> { [2945] = SszRoots.HashTreeRoot(committee) });
        LightClientHeader header = GloasHeader(slot);
        header.Beacon!.StateRoot = tree[1];
        return new() { Header = header, CurrentSyncCommittee = committee, CurrentSyncCommitteeBranch = Branch(tree, 2945) };
    }

    private static LightClientUpdate GloasUpdate(ulong finalizedSlot, ulong attestedSlot, ulong signatureSlot,
        byte key, BeaconChainSpec spec)
    {
        LightClientHeader finalized = GloasHeader(finalizedSlot);
        Hash256[] tree = Tree(new Dictionary<int, Hash256> { [735] = SszRoots.HashTreeRoot(finalized.Beacon!) });
        LightClientHeader attested = GloasHeader(attestedSlot);
        attested.Beacon!.StateRoot = tree[1];
        LightClientUpdate update = new()
        {
            FinalizedHeader = finalized,
            AttestedHeader = attested,
            SignatureSlot = signatureSlot,
            FinalityBranch = Branch(tree, 735),
            NextSyncCommittee = new SyncCommittee { Pubkeys = new BlsPublicKey[512] },
            NextSyncCommitteeBranch = Enumerable.Repeat(Hash256.Zero, 11).ToArray(),
        };
        Sign(update, key, 342, spec);
        return update;
    }

    private static LightClientHeader GloasHeader(ulong slot)
    {
        Hash256 executionHash = Hash(0x22);
        int index = slot < 32 ? 812 : 2856;
        Hash256[] tree = Tree(new Dictionary<int, Hash256> { [index] = executionHash });
        Hash256[] branch = Branch(tree, index);
        if (branch.Length < 11) branch = [.. Enumerable.Repeat(Hash256.Zero, 11 - branch.Length), .. branch];
        return new()
        {
            Beacon = new BeaconBlockHeader { Slot = slot, ParentRoot = Hash256.Zero, StateRoot = Hash256.Zero, BodyRoot = tree[1] },
            ExecutionBlockHash = executionHash,
            ExecutionBranch = branch,
            IsGloas = true,
        };
    }

    private static Hash256 Hash(byte fill) => new(Enumerable.Repeat(fill, 32).ToArray());

    private static Hash256[] Tree(Dictionary<int, Hash256> leaves)
    {
        int size = 1;
        while (size <= leaves.Keys.Max()) size <<= 1;
        Hash256[] tree = Enumerable.Repeat(Hash256.Zero, size).ToArray();
        foreach ((int index, Hash256 hash) in leaves) tree[index] = hash;
        byte[] pair = new byte[64];
        for (int i = size / 2 - 1; i > 0; i--)
        {
            if (leaves.ContainsKey(i)) continue;
            tree[i * 2].Bytes.CopyTo(pair);
            tree[i * 2 + 1].Bytes.CopyTo(pair.AsSpan(32));
            tree[i] = new Hash256(SHA256.HashData(pair));
        }
        return tree;
    }

    private static Hash256[] Branch(Hash256[] tree, int index)
    {
        List<Hash256> branch = [];
        while (index > 1)
        {
            branch.Add(tree[index ^ 1]);
            index >>= 1;
        }
        return branch.ToArray();
    }
}
