// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.LightClient.Consensus;

namespace Nethermind.LightClient.Test;

[TestFixture]
public class VerifiedConsensusJournalTests
{
    private string _directory = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(@"C:\Temp\NethermindLightClientJournalTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, recursive: true);

    [Test]
    public async Task Replays_committee_rotation_and_latest_finality_from_the_trusted_checkpoint()
    {
        LightClientBootstrap bootstrap = ConsensusTests.Bootstrap(8190, 1);
        Hash256 checkpoint = SszRoots.HashTreeRoot(bootstrap.Header!.Beacon!);
        VerifiedConsensusJournal journal = Journal(ConsensusTests.Spec, checkpoint);
        Assert.That(await journal.LoadAsync(8190, CancellationToken.None), Is.Null);
        LightClientStore store = new(ConsensusTests.Spec, checkpoint, bootstrap, 8190);
        await journal.InitializeAsync(bootstrap, store, CancellationToken.None);

        LightClientUpdate learning = ConsensusTests.Update(8190, 8190, 8191, 1, nextKey: 2);
        store.Process(learning, 8191);
        await journal.AppendAsync(learning, store, CancellationToken.None);
        LightClientUpdate rotation = ConsensusTests.Update(8192, 8193, 8194, 2);
        LightClientFinalityUpdate finality = AsFinality(rotation);
        store.Process(finality, 8194);
        await journal.AppendAsync(finality, store, CancellationToken.None);
        LightClientUpdate latest = ConsensusTests.Update(8195, 8196, 8197, 2);
        store.Process(latest, 8197);
        await journal.AppendAsync(latest, store, CancellationToken.None);

        LightClientStore? resumed = await Journal(ConsensusTests.Spec, checkpoint).LoadAsync(8197, CancellationToken.None);
        Assert.That(resumed, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(resumed!.FinalizedHeader.Beacon!.Slot, Is.EqualTo(8195));
            Assert.That(resumed.Period, Is.EqualTo(1));
            Assert.That(resumed.NextSyncCommitteeKnown, Is.False);
            Assert.That(resumed.FinalizedHeader.Execution!.StateRoot, Is.EqualTo(latest.FinalizedHeader!.Execution!.StateRoot));
        }
    }

    [Test]
    public async Task Corrupt_optional_head_falls_back_to_authenticated_committee_chain()
    {
        LightClientBootstrap bootstrap = ConsensusTests.Bootstrap(1, 1);
        Hash256 checkpoint = SszRoots.HashTreeRoot(bootstrap.Header!.Beacon!);
        VerifiedConsensusJournal journal = Journal(ConsensusTests.Spec, checkpoint);
        LightClientStore store = new(ConsensusTests.Spec, checkpoint, bootstrap, 1);
        await journal.InitializeAsync(bootstrap, store, CancellationToken.None);
        LightClientOptimisticUpdate participation = AsOptimistic(ConsensusTests.Update(1, 2, 3, 1));
        store.Process(participation, 3);
        await journal.AppendAsync(participation, store, CancellationToken.None);
        LightClientUpdate update = ConsensusTests.Update(3, 4, 5, 1);
        store.Process(update, 5);
        await journal.AppendAsync(update, store, CancellationToken.None);
        string head = Directory.GetFiles(_directory, "*.head").Single();
        byte[] bytes = await File.ReadAllBytesAsync(head);
        bytes[^1] ^= 1;
        await File.WriteAllBytesAsync(head, bytes);

        LightClientStore? resumed = await Journal(ConsensusTests.Spec, checkpoint).LoadAsync(5, CancellationToken.None);
        Assert.That(resumed!.FinalizedHeader.Beacon!.Slot, Is.EqualTo(1));
    }

    [Test]
    public async Task Replays_forced_committee_rotation_without_promoting_optimistic_state_to_finality()
    {
        LightClientBootstrap bootstrap = ConsensusTests.Bootstrap(8190, 1);
        Hash256 checkpoint = SszRoots.HashTreeRoot(bootstrap.Header!.Beacon!);
        VerifiedConsensusJournal journal = Journal(ConsensusTests.Spec, checkpoint);
        LightClientStore store = new(ConsensusTests.Spec, checkpoint, bootstrap, 8190);
        await journal.InitializeAsync(bootstrap, store, CancellationToken.None);
        LightClientUpdate learning = ConsensusTests.Update(8190, 8190, 8191, 1, nextKey: 2);
        store.Process(learning, 8191);
        await journal.AppendAsync(learning, store, CancellationToken.None);
        LightClientFinalityUpdate finality = AsFinality(ConsensusTests.Update(8191, 8191, 8192, 2));
        store.Process(finality, 8192);
        await journal.AppendAsync(finality, store, CancellationToken.None);
        LightClientUpdate weak = ConsensusTests.Update(8192, 8193, 8194, 2, participants: 341);
        store.Process(weak, 8194);
        LightClientUpdate best = store.BestUpdate!;
        Assert.That(store.ForceUpdate(16384), Is.True);
        await journal.AppendForcedAsync(best, 16384, store, CancellationToken.None);
        LightClientUpdate nextCommittee = ConsensusTests.Update(8193, 8194, 8195, 2, participants: 341, nextKey: 3);
        store.Process(nextCommittee, 16385);
        await journal.AppendAsync(nextCommittee, store, CancellationToken.None);
        best = store.BestUpdate!;
        Assert.That(store.ForceUpdate(16385), Is.True);
        await journal.AppendForcedAsync(best, 16385, store, CancellationToken.None);
        LightClientUpdate nextPeriod = ConsensusTests.Update(16384, 16385, 16386, 3, participants: 341);
        store.Process(nextPeriod, 16386);
        await journal.AppendAsync(nextPeriod, store, CancellationToken.None);
        best = store.BestUpdate!;
        Assert.That(store.ForceUpdate(16387), Is.True);
        await journal.AppendForcedAsync(best, 16387, store, CancellationToken.None);

        LightClientStore? resumed = await Journal(ConsensusTests.Spec, checkpoint).LoadAsync(16387, CancellationToken.None);
        Assert.That(resumed, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(resumed!.Period, Is.EqualTo(2));
            Assert.That(resumed.FinalizedHeader.Beacon!.Slot, Is.EqualTo(8191));
            Assert.That(resumed.OptimisticHeader.Beacon!.Slot, Is.EqualTo(16385));
        }
    }

    [Test]
    public async Task Replays_participation_maximum_across_committee_rotation_before_optimistic_reads()
    {
        LightClientBootstrap bootstrap = ConsensusTests.Bootstrap(8188, 1);
        Hash256 checkpoint = SszRoots.HashTreeRoot(bootstrap.Header!.Beacon!);
        VerifiedConsensusJournal journal = Journal(ConsensusTests.Spec, checkpoint);
        LightClientStore store = new(ConsensusTests.Spec, checkpoint, bootstrap, 8188);
        await journal.InitializeAsync(bootstrap, store, CancellationToken.None);
        LightClientUpdate learning = ConsensusTests.Update(8188, 8188, 8189, 1, participants: 342, nextKey: 2);
        store.Process(learning, 8189);
        await journal.AppendAsync(learning, store, CancellationToken.None);
        LightClientUpdate peak = ConsensusTests.Update(8188, 8190, 8191, 1, participants: 500);
        store.Process(peak, 8191);
        await journal.AppendAsync(peak, store, CancellationToken.None);
        LightClientUpdate rotation = ConsensusTests.Update(8192, 8193, 8194, 2, participants: 342);
        store.Process(rotation, 8194);
        await journal.AppendAsync(rotation, store, CancellationToken.None);
        LightClientUpdate weak = ConsensusTests.Update(8193, 8195, 8196, 2, participants: 200);
        LightClientOptimisticUpdate optimistic = AsOptimistic(weak);
        store.Process(optimistic, 8196);
        await journal.AppendAsync(optimistic, store, CancellationToken.None);

        LightClientStore? resumed = await Journal(ConsensusTests.Spec, checkpoint).LoadAsync(8196, CancellationToken.None);
        Assert.That(resumed, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.PreviousMaxParticipants, Is.EqualTo(500));
            Assert.That(resumed!.PreviousMaxParticipants, Is.EqualTo(store.PreviousMaxParticipants));
            Assert.That(resumed.CurrentMaxParticipants, Is.EqualTo(store.CurrentMaxParticipants));
            Assert.That(resumed.OptimisticHeader.Beacon!.Slot, Is.EqualTo(store.OptimisticHeader.Beacon!.Slot));
            Assert.That(resumed.OptimisticHeader.Beacon!.Slot, Is.EqualTo(8193));
        }
    }

    [Test]
    public async Task Committee_learning_preserves_an_earlier_finalized_head_from_the_optional_file()
    {
        LightClientBootstrap bootstrap = ConsensusTests.Bootstrap(1, 1);
        Hash256 checkpoint = SszRoots.HashTreeRoot(bootstrap.Header!.Beacon!);
        VerifiedConsensusJournal journal = Journal(ConsensusTests.Spec, checkpoint);
        LightClientStore store = new(ConsensusTests.Spec, checkpoint, bootstrap, 1);
        await journal.InitializeAsync(bootstrap, store, CancellationToken.None);
        LightClientOptimisticUpdate participation = AsOptimistic(ConsensusTests.Update(1, 2, 3, 1));
        store.Process(participation, 3);
        await journal.AppendAsync(participation, store, CancellationToken.None);
        LightClientFinalityUpdate finality = AsFinality(ConsensusTests.Update(3, 4, 5, 1));
        store.Process(finality, 5);
        await journal.AppendAsync(finality, store, CancellationToken.None);
        LightClientUpdate committee = ConsensusTests.Update(2, 5, 6, 1, nextKey: 2);
        store.Process(committee, 6);
        await journal.AppendAsync(committee, store, CancellationToken.None);

        LightClientStore? resumed = await Journal(ConsensusTests.Spec, checkpoint).LoadAsync(6, CancellationToken.None);
        Assert.That(resumed, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(resumed!.FinalizedHeader.Beacon!.Slot, Is.EqualTo(3));
            Assert.That(resumed.NextSyncCommitteeKnown, Is.True);
        }
    }

    [Test]
    public async Task Rejects_truncated_and_modified_committee_chains([Values] bool truncate)
    {
        LightClientBootstrap bootstrap = ConsensusTests.Bootstrap(1, 1);
        Hash256 checkpoint = SszRoots.HashTreeRoot(bootstrap.Header!.Beacon!);
        VerifiedConsensusJournal journal = Journal(ConsensusTests.Spec, checkpoint);
        LightClientStore store = new(ConsensusTests.Spec, checkpoint, bootstrap, 1);
        await journal.InitializeAsync(bootstrap, store, CancellationToken.None);
        LightClientUpdate update = ConsensusTests.Update(2, 3, 4, 1, nextKey: 2);
        store.Process(update, 4);
        await journal.AppendAsync(update, store, CancellationToken.None);
        string chain = Directory.GetFiles(_directory, "*.chain").Single();
        byte[] bytes = await File.ReadAllBytesAsync(chain);
        if (truncate) bytes = bytes[..^20];
        else
        {
            bytes[^40] ^= 1;
            SHA256.HashData(bytes.AsSpan(0, bytes.Length - 32), bytes.AsSpan(bytes.Length - 32));
        }
        await File.WriteAllBytesAsync(chain, bytes);

        Assert.That(async () => await Journal(ConsensusTests.Spec, checkpoint).LoadAsync(4, CancellationToken.None),
            Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public async Task Rejects_a_journal_copied_to_another_network()
    {
        LightClientBootstrap bootstrap = ConsensusTests.Bootstrap(1, 1);
        Hash256 checkpoint = SszRoots.HashTreeRoot(bootstrap.Header!.Beacon!);
        LightClientStore store = new(ConsensusTests.Spec, checkpoint, bootstrap, 1);
        await Journal(ConsensusTests.Spec, checkpoint).InitializeAsync(bootstrap, store, CancellationToken.None);
        string chain = Directory.GetFiles(_directory, "*.chain").Single();
        File.Copy(chain, Path.Combine(_directory, Path.GetFileName(chain).Replace("mainnet", "hoodi")));

        Assert.That(async () => await Journal(ConsensusTests.Spec, checkpoint, "hoodi").LoadAsync(1, CancellationToken.None),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("different network"));
    }

    [Test]
    public async Task Rejects_a_checkpoint_that_is_no_longer_recent_at_restart()
    {
        BeaconChainSpec spec = ConsensusTests.Spec with { GloasForkEpoch = ulong.MaxValue };
        LightClientBootstrap bootstrap = ConsensusTests.Bootstrap(1, 1);
        Hash256 checkpoint = SszRoots.HashTreeRoot(bootstrap.Header!.Beacon!);
        LightClientStore store = new(spec, checkpoint, bootstrap, 1);
        VerifiedConsensusJournal journal = Journal(spec, checkpoint);
        await journal.InitializeAsync(bootstrap, store, CancellationToken.None);
        LightClientUpdate update = ConsensusTests.Update(2, 3, 4, 1);
        store.Process(update, 4);
        await journal.AppendAsync(update, store, CancellationToken.None);

        Assert.That(async () => await Journal(spec, checkpoint).LoadAsync(100_802, CancellationToken.None),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("fourteen days"));
    }

    private VerifiedConsensusJournal Journal(BeaconChainSpec spec, Hash256 checkpoint, string network = "mainnet") =>
        new(_directory, spec, network, checkpoint);

    private static LightClientFinalityUpdate AsFinality(LightClientUpdate update) => new()
    {
        AttestedHeader = update.AttestedHeader,
        FinalizedHeader = update.FinalizedHeader,
        FinalityBranch = update.FinalityBranch,
        SyncAggregate = update.SyncAggregate,
        SignatureSlot = update.SignatureSlot,
    };

    private static LightClientOptimisticUpdate AsOptimistic(LightClientUpdate update) => new()
    {
        AttestedHeader = update.AttestedHeader,
        SyncAggregate = update.SyncAggregate,
        SignatureSlot = update.SignatureSlot,
    };
}
