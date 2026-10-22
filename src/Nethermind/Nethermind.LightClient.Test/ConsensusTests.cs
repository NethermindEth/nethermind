// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading.Channels;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.LightClient.Consensus;
using Nethermind.Int256;

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
    public void Forced_progress_restarts_the_update_timeout([Values(16292, 16293)] int currentSlot)
    {
        LightClientStore store = Store(Bootstrap(1, 1));
        store.Process(Update(8100, 8101, 8102, 1, participants: 341, nextKey: 2), 8102);
        Assert.That(store.ForceUpdate(8194), Is.True);
        store.Process(Update(8192, 8193, 8194, 2, participants: 1), 8194);

        bool advanced = store.ForceUpdate((ulong)currentSlot);

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(advanced, Is.EqualTo(currentSlot > 8100 + 8192));
        Assert.That(store.Period, Is.EqualTo(advanced ? 1 : 0));
        Assert.That(store.FinalizedHeader.Beacon!.Slot, Is.EqualTo(1));
    }

    [Test]
    public void Forced_progress_never_moves_the_sync_header_backwards()
    {
        LightClientStore store = Store(Bootstrap(8190, 1));
        store.Process(Update(8100, 8150, 8191, 1, participants: 341, nextKey: 2), 8191);
        Assert.That(store.ForceUpdate(8190 + 8193), Is.True);

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(store.NextSyncCommitteeKnown, Is.True);
        Assert.That(() => store.Process(Update(8100, 8170, 8191, 1, participants: 341), 8190 + 8193),
            Throws.TypeOf<IrrelevantLightClientUpdateException>());
    }

    [Test]
    public void Finality_older_than_forced_progress_only_advances_the_finalized_head()
    {
        LightClientStore store = Store(Bootstrap(1, 1));
        store.Process(Update(8100, 8101, 8102, 1, participants: 341, nextKey: 2), 8102);
        store.ForceUpdate(8194);
        store.Process(Update(8192, 8193, 8194, 2, participants: 1), 8194);
        Assert.That(store.ForceUpdate(16293), Is.True);

        store.Process(Update(8000, 8300, 8301, 2, nextKey: 3), 16293);

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(store.FinalizedHeader.Beacon!.Slot, Is.EqualTo(8000));
        Assert.That(store.Period, Is.EqualTo(1));
        Assert.That(store.NextSyncCommitteeKnown, Is.False);
        Assert.That(store.BestUpdate!.AttestedHeader!.Beacon!.Slot, Is.EqualTo(8300));
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
        Task CatchUp() => ConsensusSync.CatchUpAsync(store, Spec, async (period, validate, token) =>
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
            await validate(update, token);
            return update;
        }, () => 16390, (update, _) =>
        {
            store.Process(update, 16390);
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
    public void Checkpoint_age_policy_accepts_its_boundary_and_refuses_the_next_slot([Values(8191, 8192, 8193)] int ageSlots)
    {
        LightClientBootstrap bootstrap = Bootstrap(1, 1);
        Hash256 checkpoint = SszRoots.HashTreeRoot(bootstrap.Header!.Beacon!);
        BeaconChainSpec spec = Spec with { GloasForkEpoch = ulong.MaxValue };

        if (ageSlots > 8192)
            Assert.That(() => new LightClientStore(spec, checkpoint, bootstrap, (ulong)ageSlots + 1), Throws.TypeOf<LightClientLocalStateException>().With.Message.Contains("256-epoch"));
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
        ulong currentSlot = invalid == 1 ? 0 : invalid == 2 ? 8194UL : 1;
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
    public async Task Stale_or_future_consensus_retries_within_one_slot([Values(0UL, 21UL, ulong.MaxValue)] ulong optimisticSlot)
    {
        RepairClock clock = new();
        using CancellationTokenSource stop = new();
        int repairs = 0;
        Task loop = ConsensusSync.RunRepairLoopAsync(_ => { repairs++; return Task.CompletedTask; },
            () => optimisticSlot, static () => 20, TimeSpan.FromSeconds(12), clock, stop.Token);
        try
        {
            await clock.NextTimerAsync();
            Assert.That(repairs, Is.EqualTo(1));
            Assert.That(clock.Advance(TimeSpan.FromSeconds(11)), Is.False);
            Assert.That(repairs, Is.EqualTo(1), "an unavailable peer must not cause a retry spin");
            Assert.That(clock.Advance(TimeSpan.FromSeconds(1)), Is.True, "stale consensus must retry within one slot");
            await clock.NextTimerAsync();
            Assert.That(repairs, Is.EqualTo(2));
        }
        finally { await StopRepairLoopAsync(loop, stop); }
        Assert.That(clock.ActiveTimers, Is.Zero);
    }

    [Test]
    public async Task Missing_gossip_exits_healthy_backoff_when_consensus_becomes_stale()
    {
        RepairClock clock = new();
        using CancellationTokenSource stop = new();
        ulong optimisticSlot = 100;
        int repairs = 0;
        ulong CurrentSlot() => 100 + (ulong)clock.GetUtcNow().ToUnixTimeSeconds() / 12;
        Task loop = ConsensusSync.RunRepairLoopAsync(_ =>
        {
            repairs++;
            if (repairs > 1) optimisticSlot = CurrentSlot();
            return Task.CompletedTask;
        }, () => optimisticSlot, CurrentSlot, TimeSpan.FromSeconds(12), clock, stop.Token);
        try
        {
            await clock.NextTimerAsync();
            for (int slot = 1; slot <= 3; slot++)
            {
                Assert.That(clock.Advance(TimeSpan.FromSeconds(12)), Is.True);
                await clock.NextTimerAsync();
                Assert.That(repairs, Is.EqualTo(slot <= 2 ? 1 : 2));
            }
            using (Assert.EnterMultipleScope())
            {
                Assert.That(optimisticSlot, Is.EqualTo(103));
                Assert.That(clock.GetTimestamp(), Is.EqualTo(TimeSpan.FromSeconds(36).Ticks));
            }
        }
        finally { await StopRepairLoopAsync(loop, stop); }
    }

    [Test]
    public async Task Healthy_gossip_keeps_the_one_minute_repair_backoff([Values(0UL, 100UL, ulong.MaxValue - 10)] ulong initialSlot)
    {
        RepairClock clock = new();
        using CancellationTokenSource stop = new();
        ulong slot = initialSlot;
        int repairs = 0;
        Task loop = ConsensusSync.RunRepairLoopAsync(_ => { repairs++; return Task.CompletedTask; },
            () => slot, () => slot, TimeSpan.FromSeconds(12), clock, stop.Token);
        try
        {
            await clock.NextTimerAsync();
            for (int elapsed = 12; elapsed <= 60; elapsed += 12)
            {
                slot++;
                Assert.That(clock.Advance(TimeSpan.FromSeconds(12)), Is.True);
                await clock.NextTimerAsync();
                Assert.That(repairs, Is.EqualTo(elapsed < 60 ? 1 : 2));
            }
        }
        finally { await StopRepairLoopAsync(loop, stop); }
    }

    [Test]
    public async Task A_stalled_repair_keeps_its_deadline_and_waits_one_slot_after_failure()
    {
        RepairClock clock = new();
        using CancellationTokenSource stop = new();
        int repairs = 0;
        int expired = 0;
        Task loop = ConsensusSync.RunRepairLoopAsync(async token =>
        {
            repairs++;
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15), clock);
            using CancellationTokenSource attempt = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, attempt.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { expired++; }
        }, static () => 0, static () => 100, TimeSpan.FromSeconds(12), clock, stop.Token);
        try
        {
            await clock.NextTimerAsync();
            Assert.That(clock.Advance(TimeSpan.FromSeconds(14)), Is.False);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(repairs, Is.EqualTo(1));
                Assert.That(expired, Is.Zero);
            }
            Assert.That(clock.Advance(TimeSpan.FromSeconds(1)), Is.True);
            await clock.NextTimerAsync();
            Assert.That(expired, Is.EqualTo(1));
            Assert.That(clock.Advance(TimeSpan.FromSeconds(11)), Is.False);
            Assert.That(repairs, Is.EqualTo(1));
            Assert.That(clock.Advance(TimeSpan.FromSeconds(1)), Is.True);
            await clock.NextTimerAsync();
            Assert.That(repairs, Is.EqualTo(2));
        }
        finally { await StopRepairLoopAsync(loop, stop); }
        Assert.That(clock.ActiveTimers, Is.Zero);
    }

    [Test]
    public async Task Repair_waits_for_peer_recovery_without_overlapping_or_catching_up_missed_ticks()
    {
        RepairClock clock = new();
        using CancellationTokenSource stop = new();
        TaskCompletionSource peerAvailable = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int repairs = 0;
        ulong head = 0;
        Task loop = ConsensusSync.RunRepairLoopAsync(async token =>
        {
            repairs++;
            await peerAvailable.Task.WaitAsync(token);
            head = 100;
        }, () => head, static () => 100, TimeSpan.FromSeconds(12), clock, stop.Token);
        try
        {
            clock.Advance(TimeSpan.FromSeconds(90));
            Assert.That(repairs, Is.EqualTo(1), "a pending peer request remains the only repair owner");
            peerAvailable.SetResult();
            await clock.NextTimerAsync();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(head, Is.EqualTo(100));
                Assert.That(repairs, Is.EqualTo(1));
            }
            Assert.That(clock.Advance(TimeSpan.FromSeconds(12)), Is.True);
            await clock.NextTimerAsync();
            Assert.That(repairs, Is.EqualTo(1), "healthy recovery restarts the backoff instead of replaying missed ticks");
        }
        finally { await StopRepairLoopAsync(loop, stop); }
    }

    [Test]
    public async Task Repair_loop_cancellation_joins_pending_work([Values] bool repairing)
    {
        RepairClock clock = new();
        using CancellationTokenSource stop = new();
        TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task loop = ConsensusSync.RunRepairLoopAsync(async token =>
        {
            if (!repairing) return;
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { released.SetResult(); }
        }, static () => 0, static () => 10, TimeSpan.FromSeconds(12), clock, stop.Token);
        if (!repairing) await clock.NextTimerAsync();
        await StopRepairLoopAsync(loop, stop);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(released.Task.IsCompleted, Is.EqualTo(repairing));
            Assert.That(clock.ActiveTimers, Is.Zero);
        }
    }

    [Test]
    public void Already_canceled_repair_loop_does_not_request_data()
    {
        using CancellationTokenSource stop = new();
        stop.Cancel();
        int repairs = 0;
        Assert.That(async () => await ConsensusSync.RunRepairLoopAsync(_ => { repairs++; return Task.CompletedTask; },
            static () => 0, static () => 10, TimeSpan.FromSeconds(12), new RepairClock(), stop.Token), Throws.InstanceOf<OperationCanceledException>());
        Assert.That(repairs, Is.Zero);
    }

    [Test]
    public async Task Held_finalized_resolution_does_not_delay_a_persisted_optimistic_slot_at_the_same_execution_hash()
    {
        PublicationHarness harness = new();
        await harness.SeedAsync();
        harness.Finalized = PublicationHeader(100, 90);
        harness.Optimistic = PublicationHeader(198, 120);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Resolve = async (header, token) =>
        {
            Assert.That(header.ExecutionBlockHash, Is.EqualTo(harness.Finalized.ExecutionBlockHash),
                "the already resolved optimistic hash needs no execution request");
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Canceled resolution cannot return a header.");
        };
        using CancellationTokenSource stop = new();
        Task loop = harness.Start(stop.Token);
        try
        {
            await entered.Task;
            PublicationSource source = new();
            using VerifiedRpc rpc = new(source, () => harness.Publisher.Heads.Finalized!, 1,
                getLatestHead: () => harness.Publisher.Heads.Latest,
                isLatestHeadFresh: head => head.IsFresh(200, 10));
            object syncing = await rpc.InvokeAsync("eth_syncing", default, CancellationToken.None);
            object number = await rpc.InvokeAsync("eth_blockNumber", default, CancellationToken.None);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(harness.Publisher.Heads.Finalized!.Slot, Is.EqualTo(80));
                Assert.That(harness.Publisher.Heads.Latest, Is.EqualTo(ResolvedPublicationHeader(harness.Optimistic).Head));
                Assert.That(syncing, Is.EqualTo(false));
                Assert.That(number, Is.EqualTo("0x78"));
                Assert.That(source.Requests, Is.Zero);
                Assert.That(harness.Observations.Select(snapshot => snapshot.Latest!.Slot), Is.EqualTo(new ulong[] { 198 }));
                Assert.That(harness.Advertised, Is.Empty);
                Assert.That(harness.Failures, Is.Empty);
            }
        }
        finally { await StopRepairLoopAsync(loop, stop); }
        Assert.That(harness.Clock.ActiveTimers, Is.Zero);
    }

    [Test]
    public async Task Publication_commits_preserve_newer_optimistic_heads_and_the_finalized_floor(
        [Values] bool finalizedFirst, [Values("none", "slot", "number")] string floor)
    {
        PublicationHarness harness = new();
        await harness.SeedAsync();
        harness.Finalized = PublicationHeader(floor == "slot" ? 200UL : 190UL, floor == "number" ? (byte)150 : (byte)130);
        harness.Optimistic = PublicationHeader(198, 140);
        TaskCompletionSource<(VerifiedHead Head, ulong Timestamp)> finalized = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<(VerifiedHead Head, ulong Timestamp)> optimistic = new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Resolve = (header, token) => (header.ExecutionBlockHash == harness.Finalized.ExecutionBlockHash
            ? finalized.Task : optimistic.Task).WaitAsync(token);
        using CancellationTokenSource stop = new();
        Task loop = harness.Start(stop.Token);
        try
        {
            if (finalizedFirst) finalized.SetResult(ResolvedPublicationHeader(harness.Finalized));
            else optimistic.SetResult(ResolvedPublicationHeader(harness.Optimistic));
            await harness.Clock.WaitForTimersAsync(2);
            Assert.That(harness.Publisher.Heads.Latest!.Slot,
                Is.EqualTo(finalizedFirst ? harness.Finalized.Beacon!.Slot : 198UL));
            if (finalizedFirst) optimistic.SetResult(ResolvedPublicationHeader(harness.Optimistic));
            else finalized.SetResult(ResolvedPublicationHeader(harness.Finalized));
            await harness.Clock.WaitForTimersAsync(3);

            VerifiedHead expectedFinalized = ResolvedPublicationHeader(harness.Finalized).Head;
            VerifiedHead expectedLatest = floor == "none" ? ResolvedPublicationHeader(harness.Optimistic).Head : expectedFinalized;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(harness.Publisher.Heads.Finalized, Is.EqualTo(expectedFinalized));
                Assert.That(harness.Publisher.Heads.Latest, Is.EqualTo(expectedLatest));
                Assert.That(harness.Publisher.Heads.Latest!.Slot, Is.GreaterThanOrEqualTo(expectedFinalized.Slot));
                Assert.That(harness.Publisher.Heads.Latest!.Number, Is.GreaterThanOrEqualTo(expectedFinalized.Number));
                Assert.That(harness.Advertised, Is.EqualTo(new[] { ResolvedPublicationHeader(harness.Finalized) }));
                Assert.That(harness.Failures, Is.Empty);
            }
        }
        finally { await StopRepairLoopAsync(loop, stop); }
    }

    [Test]
    public async Task A_newer_optimistic_branch_can_lower_execution_height_above_finality()
    {
        PublicationHarness harness = new();
        await harness.SeedAsync();
        harness.Optimistic = PublicationHeader(198, 110, branch: 1);
        using CancellationTokenSource stop = new();
        Task loop = harness.Start(stop.Token);
        try
        {
            await harness.Clock.WaitForTimersAsync(3);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(harness.Publisher.Heads.Latest, Is.EqualTo(ResolvedPublicationHeader(harness.Optimistic).Head));
                Assert.That(harness.Publisher.Heads.Latest!.Number, Is.LessThan(120));
                Assert.That(harness.Publisher.Heads.Latest!.Number, Is.GreaterThan(harness.Publisher.Heads.Finalized!.Number));
                Assert.That(harness.Advertised, Is.Empty);
            }
        }
        finally { await StopRepairLoopAsync(loop, stop); }
    }

    [Test]
    public async Task Same_hash_publication_reuses_execution_metadata_at_a_higher_beacon_slot([Values] bool finalized)
    {
        PublicationHarness harness = new();
        await harness.SeedAsync();
        if (finalized) harness.Finalized = PublicationHeader(198, 80);
        else harness.Optimistic = PublicationHeader(198, 120);
        harness.Resolve = (_, _) => throw new InvalidOperationException("An unchanged execution hash must not be fetched again.");
        using CancellationTokenSource stop = new();
        Task loop = harness.Start(stop.Token);
        try
        {
            await harness.Clock.WaitForTimersAsync(3);
            ConsensusPublisher.PublishedHeads snapshot = harness.Publisher.Heads;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(snapshot.Latest!.Slot, Is.EqualTo(198));
                Assert.That(snapshot.Latest.Number, Is.EqualTo(finalized ? 80 : 120));
                Assert.That(snapshot.Latest.IsFresh(200, 10), Is.True);
                Assert.That(snapshot.FinalizedTimestamp, Is.EqualTo(PublicationTimestamp));
                Assert.That(harness.Advertised.Count, Is.EqualTo(finalized ? 1 : 0));
                Assert.That(harness.Failures, Is.Empty);
            }
        }
        finally { await StopRepairLoopAsync(loop, stop); }
    }

    [Test]
    public async Task Publication_failure_retains_heads_and_retries_after_one_virtual_second([Values] bool finalized)
    {
        PublicationHarness harness = new();
        await harness.SeedAsync();
        ConsensusPublisher.PublishedHeads before = harness.Publisher.Heads;
        if (finalized) harness.Finalized = PublicationHeader(190, 130);
        else harness.Optimistic = PublicationHeader(198, 140);
        IOException failure = new("Execution header is unavailable.");
        harness.Resolve = (_, _) => throw failure;
        using CancellationTokenSource stop = new();
        Task loop = harness.Start(stop.Token);
        try
        {
            await harness.Clock.WaitForTimersAsync(3);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(harness.Publisher.Heads, Is.EqualTo(before));
                Assert.That(harness.Failures, Is.EqualTo(new[] { (finalized, (Exception)failure) }));
                Assert.That(harness.Advertised, Is.Empty);
            }
            harness.Resolve = static (header, _) => Task.FromResult(ResolvedPublicationHeader(header));
            Assert.That(harness.Clock.Advance(TimeSpan.FromSeconds(1)), Is.True);
            await harness.Clock.WaitForTimersAsync(3);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(finalized ? harness.Publisher.Heads.Finalized : harness.Publisher.Heads.Latest,
                    Is.EqualTo(ResolvedPublicationHeader(finalized ? harness.Finalized : harness.Optimistic).Head));
                Assert.That(harness.Advertised.Count, Is.EqualTo(finalized ? 1 : 0));
                Assert.That(harness.Failures.Count, Is.EqualTo(1));
            }
        }
        finally { await StopRepairLoopAsync(loop, stop); }
    }

    [Test]
    public async Task Publication_shutdown_cancels_and_joins_inflight_resolution([Values] bool finalized)
    {
        PublicationHarness harness = new();
        await harness.SeedAsync();
        ConsensusPublisher.PublishedHeads before = harness.Publisher.Heads;
        if (finalized) harness.Finalized = PublicationHeader(190, 130);
        else harness.Optimistic = PublicationHeader(198, 140);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Resolve = async (header, token) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                canceled.TrySetResult();
                await release.Task;
            }
            return ResolvedPublicationHeader(header);
        };
        using CancellationTokenSource stop = new();
        Task loop = harness.Start(stop.Token);
        try
        {
            await entered.Task;
            await stop.CancelAsync();
            await canceled.Task;
            Assert.That(loop.IsCompleted, Is.False, "shutdown must join the resolver's cleanup");
        }
        finally
        {
            release.TrySetResult();
            await StopRepairLoopAsync(loop, stop);
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.Publisher.Heads, Is.EqualTo(before));
            Assert.That(harness.Advertised, Is.Empty);
            Assert.That(harness.Failures, Is.Empty);
            Assert.That(harness.Clock.ActiveTimers, Is.Zero);
        }
    }

    [Test]
    public async Task An_observer_failure_cancels_and_joins_sibling_publication_before_propagating()
    {
        PublicationHarness harness = new();
        await harness.SeedAsync();
        harness.Finalized = PublicationHeader(190, 130);
        TaskCompletionSource canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Resolve = async (header, token) =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally
            {
                canceled.TrySetResult();
                await release.Task;
            }
            return ResolvedPublicationHeader(header);
        };
        InvalidOperationException failure = new("Observer failed.");
        harness.Observe = _ => throw failure;
        using CancellationTokenSource stop = new();
        Task loop = harness.Start(stop.Token);
        Exception? propagated = null;
        try
        {
            await canceled.Task;
            Assert.That(loop.IsCompleted, Is.False);
        }
        finally
        {
            release.TrySetResult();
            try { await loop; }
            catch (Exception exception) { propagated = exception; }
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(propagated, Is.SameAs(failure));
            Assert.That(stop.IsCancellationRequested, Is.False, "the publication owner cancels its own siblings");
            Assert.That(harness.Advertised, Is.Empty);
            Assert.That(harness.Clock.ActiveTimers, Is.Zero);
        }
    }

    [Test]
    public async Task Already_canceled_publication_does_not_read_or_observe_candidates()
    {
        RepairClock clock = new();
        int callbacks = 0;
        ConsensusPublisher publisher = new(() => { callbacks++; return PublicationHeader(80, 80); },
            () => { callbacks++; return PublicationHeader(180, 120); },
            (header, _) => { callbacks++; return Task.FromResult(ResolvedPublicationHeader(header)); },
            (_, _) => callbacks++, clock);
        using CancellationTokenSource stop = new();
        await stop.CancelAsync();
        await publisher.RunAsync(_ => callbacks++, (_, _) => callbacks++, stop.Token);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(callbacks, Is.Zero);
            Assert.That(publisher.Heads.Finalized, Is.Null);
            Assert.That(publisher.Heads.Latest, Is.Null);
            Assert.That(clock.ActiveTimers, Is.Zero);
        }
    }

    private const ulong PublicationTimestamp = 12345;

    private static LightClientHeader PublicationHeader(ulong slot, byte number, byte branch = 0)
    {
        byte[] hash = new byte[32];
        hash[0] = number;
        hash[1] = branch;
        return new() { Beacon = new() { Slot = slot }, ExecutionBlockHash = new(hash) };
    }

    private static (VerifiedHead Head, ulong Timestamp) ResolvedPublicationHeader(LightClientHeader header) =>
        (new(header.Beacon!.Slot, header.ExecutionBlockHash!.Bytes[0], header.ExecutionBlockHash, Hash(0x21)), PublicationTimestamp);

    private sealed class PublicationSource : IExecutionStateSource
    {
        internal int Requests;
        public Task<Account> GetAccountAsync(VerifiedHead head, Address address, CancellationToken cancellationToken) => Unexpected<Account>();
        public Task<UInt256> GetStorageAsync(VerifiedHead head, Address address, Account account, UInt256 key, CancellationToken cancellationToken) => Unexpected<UInt256>();
        public Task<byte[]> GetCodeAsync(Account account, CancellationToken cancellationToken) => Unexpected<byte[]>();
        public Task<BlockHeader> GetHeaderAsync(VerifiedHead head, CancellationToken cancellationToken) => Unexpected<BlockHeader>();
        public Task<Hash256[]> GetAncestorHashesAsync(VerifiedHead head, ulong firstNumber, CancellationToken cancellationToken) => Unexpected<Hash256[]>();

        private Task<T> Unexpected<T>()
        {
            Interlocked.Increment(ref Requests);
            throw new InvalidOperationException("Head freshness RPC must not request execution data.");
        }
    }

    private sealed class PublicationHarness
    {
        private LightClientHeader _finalized = PublicationHeader(80, 80);
        private LightClientHeader _optimistic = PublicationHeader(180, 120);
        private Func<LightClientHeader, CancellationToken, Task<(VerifiedHead Head, ulong Timestamp)>> _resolve =
            static (header, _) => Task.FromResult(ResolvedPublicationHeader(header));

        internal RepairClock Clock { get; } = new();
        internal ConsensusPublisher Publisher { get; }
        internal ConcurrentQueue<ConsensusPublisher.PublishedHeads> Observations { get; } = new();
        internal ConcurrentQueue<(VerifiedHead Head, ulong Timestamp)> Advertised { get; } = new();
        internal ConcurrentQueue<(bool Finalized, Exception Failure)> Failures { get; } = new();
        internal Action<ConsensusPublisher.PublishedHeads>? Observe { get; set; }
        internal LightClientHeader Finalized { get => Volatile.Read(ref _finalized); set => Volatile.Write(ref _finalized, value); }
        internal LightClientHeader Optimistic { get => Volatile.Read(ref _optimistic); set => Volatile.Write(ref _optimistic, value); }
        internal Func<LightClientHeader, CancellationToken, Task<(VerifiedHead Head, ulong Timestamp)>> Resolve
        {
            set => Volatile.Write(ref _resolve, value);
        }

        internal PublicationHarness() => Publisher = new(() => Finalized, () => Optimistic,
            (header, token) => Volatile.Read(ref _resolve)(header, token),
            (head, timestamp) => Advertised.Enqueue((head, timestamp)), Clock);

        internal Task Start(CancellationToken token) => Publisher.RunAsync(snapshot =>
        {
            Observations.Enqueue(snapshot);
            Observe?.Invoke(snapshot);
        }, (finalized, exception) => Failures.Enqueue((finalized, exception)), token);

        internal async Task SeedAsync()
        {
            await Publisher.PublishFinalizedAsync(CancellationToken.None);
            using CancellationTokenSource stop = new();
            Task loop = Start(stop.Token);
            await StopRepairLoopAsync(loop, stop);
            Observations.Clear();
            Advertised.Clear();
            Failures.Clear();
        }
    }

    private static async Task StopRepairLoopAsync(Task loop, CancellationTokenSource stop)
    {
        await stop.CancelAsync();
        try { await loop; }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    private sealed class RepairClock : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<Timer> _timers = [];
        private readonly Channel<TimeSpan> _created = Channel.CreateUnbounded<TimeSpan>();
        private long _ticks;
        internal int ActiveTimers { get { lock (_gate) return _timers.Count; } }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());
        internal async Task NextTimerAsync() => await _created.Reader.ReadAsync();
        internal async Task WaitForTimersAsync(int count)
        {
            while (ActiveTimers < count) await NextTimerAsync();
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Timer timer = new(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        internal bool Advance(TimeSpan elapsed)
        {
            List<Timer> due;
            lock (_gate)
            {
                _ticks += elapsed.Ticks;
                due = _timers.Where(timer => timer.Due <= _ticks).ToList();
                foreach (Timer timer in due) _timers.Remove(timer);
            }
            foreach (Timer timer in due) timer.Fire();
            return due.Count > 0;
        }

        private sealed class Timer(RepairClock clock, TimerCallback callback, object? state) : ITimer
        {
            internal long Due;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock._gate)
                {
                    clock._timers.Remove(this);
                    if (dueTime != Timeout.InfiniteTimeSpan)
                    {
                        Due = clock._ticks + dueTime.Ticks;
                        clock._timers.Add(this);
                        clock._created.Writer.TryWrite(dueTime);
                    }
                }
                return true;
            }
            internal void Fire() => callback(state);
            public void Dispose() { lock (clock._gate) clock._timers.Remove(this); }
            public ValueTask DisposeAsync() { Dispose(); return default; }
        }
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

    internal static void Sign(LightClientUpdate update, byte key, int participants, BeaconChainSpec spec, ulong? domainSlot = null)
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

    internal static Hash256[] Tree(Dictionary<int, Hash256> leaves)
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

    internal static Hash256[] Branch(Hash256[] tree, int index)
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
