// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.LightClient.Consensus;

namespace Nethermind.LightClient.Test;

[TestFixture]
public class VerifiedConsensusJournalTests
{
    private string _directory = null!;
    private RecordingLogger _logger = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(OperatingSystem.IsWindows() ? "C:/Temp" : Path.GetTempPath(),
            "NethermindLightClientJournalTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _logger = new RecordingLogger();
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
            Assert.That(_logger.Entries, Is.Empty);
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
        Assert.That(_logger.Entries, Has.Count.EqualTo(2));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(resumed!.FinalizedHeader.Beacon!.Slot, Is.EqualTo(1));
            Assert.That(_logger.Entries[0].Level, Is.EqualTo(LogLevel.Warning));
            Assert.That(_logger.Entries[0].Message, Does.Contain(head));
            Assert.That(_logger.Entries[0].Exception, Is.TypeOf<InvalidDataException>());
        }
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

    [TestCase(8195, true)]
    [TestCase(8196, false)]
    public async Task Restart_requires_recent_authenticated_state_rather_than_a_recent_checkpoint(int currentSlot, bool accepted)
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

        Task<LightClientStore?> Load() => Journal(spec, checkpoint).LoadAsync((ulong)currentSlot, CancellationToken.None);

        if (accepted)
            Assert.That((await Load())!.FinalizedHeader.Beacon!.Slot, Is.EqualTo(2));
        else
            Assert.That(async () => await Load(), Throws.TypeOf<LightClientLocalStateException>().With.Message.Contains("256-epoch"));
    }

    [Test]
    public async Task Committee_chain_record_supersedes_the_saved_head()
    {
        LightClientBootstrap bootstrap = ConsensusTests.Bootstrap(1, 1);
        Hash256 checkpoint = SszRoots.HashTreeRoot(bootstrap.Header!.Beacon!);
        VerifiedConsensusJournal journal = Journal(ConsensusTests.Spec, checkpoint);
        LightClientStore store = new(ConsensusTests.Spec, checkpoint, bootstrap, 1);
        await journal.InitializeAsync(bootstrap, store, CancellationToken.None);
        LightClientOptimisticUpdate participation = AsOptimistic(ConsensusTests.Update(1, 2, 3, 1));
        store.Process(participation, 3);
        await journal.AppendAsync(participation, store, CancellationToken.None);
        LightClientUpdate head = ConsensusTests.Update(3, 4, 5, 1);
        store.Process(head, 5);
        await journal.AppendAsync(head, store, CancellationToken.None);
        Assert.That(Directory.GetFiles(_directory, "*.head"), Has.Length.EqualTo(1));
        LightClientUpdate learning = ConsensusTests.Update(5, 6, 7, 1, nextKey: 2);
        store.Process(learning, 7);
        await journal.AppendAsync(learning, store, CancellationToken.None);

        LightClientStore? resumed = await Journal(ConsensusTests.Spec, checkpoint).LoadAsync(7, CancellationToken.None);

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(Directory.GetFiles(_directory, "*.head"), Is.Empty);
        Assert.That(_logger.Entries, Is.Empty);
        Assert.That(resumed!.FinalizedHeader.Beacon!.Slot, Is.EqualTo(5));
        Assert.That(resumed.NextSyncCommitteeKnown, Is.True);
    }

    [Test]
    public async Task Latest_optimistic_progress_survives_restart_and_a_later_committee_record(
        [Values("full", "finality", "optimistic")] string shape)
    {
        (LightClientStore store, VerifiedConsensusJournal journal, Hash256 checkpoint) = await JournalWithOptionalFinalizedHeadAsync();
        LightClientUpdate later = ConsensusTests.Update(4, 7, 8, 1);
        switch (shape)
        {
            case "full":
                store.Process(later, 8);
                await journal.AppendAsync(later, store, CancellationToken.None);
                break;
            case "finality":
                LightClientFinalityUpdate finality = AsFinality(later);
                store.Process(finality, 8);
                await journal.AppendAsync(finality, store, CancellationToken.None);
                break;
            case "optimistic":
                LightClientOptimisticUpdate optimistic = AsOptimistic(later);
                store.Process(optimistic, 8);
                await journal.AppendAsync(optimistic, store, CancellationToken.None);
                break;
        }
        LightClientStore? resumed = await Journal(ConsensusTests.Spec, checkpoint).LoadAsync(8, CancellationToken.None);
        Assert.That(resumed, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(resumed!.FinalizedHeader.Beacon!.Slot, Is.EqualTo(4));
            Assert.That(resumed.OptimisticHeader.Beacon!.Slot, Is.EqualTo(7));
            Assert.That(resumed.OptimisticHeader.Execution!.BlockHash, Is.EqualTo(later.AttestedHeader!.Execution!.BlockHash));
            Assert.That(resumed.CurrentMaxParticipants, Is.EqualTo(store.CurrentMaxParticipants));
            Assert.That(Directory.GetFiles(_directory, "*.optimistic"), Has.Length.EqualTo(1));
            Assert.That(_logger.Entries, Is.Empty);
        }

        LightClientUpdate committee = ConsensusTests.Update(4, 8, 9, 1, nextKey: 2);
        store.Process(committee, 9);
        await journal.AppendAsync(committee, store, CancellationToken.None);
        resumed = await Journal(ConsensusTests.Spec, checkpoint).LoadAsync(9, CancellationToken.None);
        Assert.That(resumed, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(resumed!.FinalizedHeader.Beacon!.Slot, Is.EqualTo(4));
            Assert.That(resumed.OptimisticHeader.Beacon!.Slot, Is.EqualTo(8));
            Assert.That(resumed.NextSyncCommitteeKnown, Is.True);
            Assert.That(Directory.GetFiles(_directory, "*.head"), Is.Empty);
            Assert.That(Directory.GetFiles(_directory, "*.optimistic"), Is.Empty);
            Assert.That(_logger.Entries, Is.Empty);
        }
    }

    [Test]
    public async Task Corrupt_optimistic_head_preserves_the_latest_authenticated_finalized_head()
    {
        (LightClientStore store, VerifiedConsensusJournal journal, Hash256 checkpoint) = await JournalWithOptionalFinalizedHeadAsync();
        LightClientOptimisticUpdate optimistic = AsOptimistic(ConsensusTests.Update(4, 7, 8, 1));
        store.Process(optimistic, 8);
        await journal.AppendAsync(optimistic, store, CancellationToken.None);
        string path = Directory.GetFiles(_directory, "*.optimistic").Single();
        byte[] bytes = await File.ReadAllBytesAsync(path);
        bytes[^1] ^= 1;
        await File.WriteAllBytesAsync(path, bytes);

        LightClientStore? resumed = await Journal(ConsensusTests.Spec, checkpoint).LoadAsync(8, CancellationToken.None);
        Assert.That(resumed, Is.Not.Null);
        Assert.That(_logger.Entries, Has.Count.EqualTo(2));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(resumed!.FinalizedHeader.Beacon!.Slot, Is.EqualTo(4));
            Assert.That(resumed.OptimisticHeader.Beacon!.Slot, Is.EqualTo(5));
            Assert.That(_logger.Entries[0].Exception, Is.TypeOf<InvalidDataException>());
            Assert.That(_logger.Entries[0].Message, Does.Contain(path));
        }
    }

    [TestCase(8199UL, true)]
    [TestCase(8200UL, false)]
    public async Task Restart_age_uses_latest_optimistic_progress_even_when_participation_is_unchanged(ulong currentSlot, bool accepted)
    {
        BeaconChainSpec spec = ConsensusTests.Spec with { GloasForkEpoch = ulong.MaxValue };
        LightClientBootstrap bootstrap = ConsensusTests.Bootstrap(1, 1);
        Hash256 checkpoint = SszRoots.HashTreeRoot(bootstrap.Header!.Beacon!);
        LightClientStore store = new(spec, checkpoint, bootstrap, 1);
        VerifiedConsensusJournal journal = Journal(spec, checkpoint);
        await journal.InitializeAsync(bootstrap, store, CancellationToken.None);
        LightClientOptimisticUpdate peak = AsOptimistic(ConsensusTests.Update(1, 2, 3, 1));
        store.Process(peak, 3);
        await journal.AppendAsync(peak, store, CancellationToken.None);
        LightClientOptimisticUpdate latest = AsOptimistic(ConsensusTests.Update(1, 7, 8, 1));
        store.Process(latest, 8);
        await journal.AppendAsync(latest, store, CancellationToken.None);

        Task<LightClientStore?> Load() => Journal(spec, checkpoint).LoadAsync(currentSlot, CancellationToken.None);
        if (accepted) Assert.That((await Load())!.OptimisticHeader.Beacon!.Slot, Is.EqualTo(7));
        else Assert.That(async () => await Load(), Throws.TypeOf<LightClientLocalStateException>().With.Message.Contains("256-epoch"));
    }

    [Test]
    public async Task A_better_committee_candidate_survives_restart_and_later_optimistic_progress(
        [Values] bool advanceOptimistic)
    {
        (LightClientStore store, VerifiedConsensusJournal journal, Hash256 checkpoint) = await JournalWithWeakCandidateAsync();
        if (advanceOptimistic)
        {
            LightClientOptimisticUpdate later = AsOptimistic(ConsensusTests.Update(1, 200, 201, 1, participants: 100));
            store.Process(later, 201);
            await journal.AppendAsync(later, store, CancellationToken.None);
        }
        LightClientStore resumed = (await Journal(ConsensusTests.Spec, checkpoint).LoadAsync(8194, CancellationToken.None))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resumed.FinalizedHeader.Beacon!.Slot, Is.EqualTo(1));
            Assert.That(resumed.OptimisticHeader.Beacon!.Slot, Is.EqualTo(advanceOptimistic ? 200 : 100));
            Assert.That(resumed.BestUpdate!.SignatureSlot, Is.EqualTo(51));
            Assert.That(resumed.BestUpdate.AttestedHeader!.Beacon!.Slot, Is.EqualTo(50));
            Assert.That(resumed.CurrentMaxParticipants, Is.EqualTo(100));
            Assert.That(resumed.NextSyncCommitteeKnown, Is.False);
            Assert.That(_logger.Entries, Is.Empty);
        }
        Assert.That(resumed.ForceUpdate(8194), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(resumed.NextSyncCommitteeKnown, Is.True);
            Assert.That(resumed.FinalizedHeader.Beacon!.Slot, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task A_candidate_received_after_the_optional_finalized_head_restores_without_changing_other_state()
    {
        (LightClientStore store, VerifiedConsensusJournal journal, Hash256 checkpoint) = await JournalWithOptionalFinalizedHeadAsync();
        LightClientUpdate weak = ConsensusTests.Update(5, 50, 51, 1, participants: 100, nextKey: 2);
        store.Process(weak, 51);
        await journal.AppendAsync(weak, store, CancellationToken.None);
        LightClientStore resumed = (await Journal(ConsensusTests.Spec, checkpoint).LoadAsync(8197, CancellationToken.None))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resumed.FinalizedHeader.Beacon!.Slot, Is.EqualTo(store.FinalizedHeader.Beacon!.Slot));
            Assert.That(resumed.OptimisticHeader.Beacon!.Slot, Is.EqualTo(store.OptimisticHeader.Beacon!.Slot));
            Assert.That(resumed.CurrentMaxParticipants, Is.EqualTo(store.CurrentMaxParticipants));
            Assert.That(resumed.PreviousMaxParticipants, Is.EqualTo(store.PreviousMaxParticipants));
            Assert.That(resumed.NextSyncCommitteeKnown, Is.False);
            Assert.That(resumed.BestUpdate!.SignatureSlot, Is.EqualTo(51));
        }
        Assert.That(resumed.ForceUpdate(8197), Is.True);
        Assert.That(resumed.NextSyncCommitteeKnown, Is.True);
        Assert.That(resumed.FinalizedHeader.Beacon!.Slot, Is.EqualTo(4));
    }

    [Test]
    public async Task A_candidate_cleared_by_finality_cannot_be_resurrected_by_a_stale_side_file()
    {
        (LightClientStore store, VerifiedConsensusJournal journal, Hash256 checkpoint) = await JournalWithOptionalFinalizedHeadAsync();
        LightClientUpdate weak = ConsensusTests.Update(5, 50, 51, 1, participants: 100, nextKey: 2);
        store.Process(weak, 51);
        await journal.AppendAsync(weak, store, CancellationToken.None);
        string path = Directory.GetFiles(_directory, "*.best").Single();
        byte[] previous = await File.ReadAllBytesAsync(path);
        LightClientFinalityUpdate finality = AsFinality(ConsensusTests.Update(6, 60, 61, 1));
        store.Process(finality, 61);
        await journal.AppendAsync(finality, store, CancellationToken.None);
        Assert.That(store.BestUpdate, Is.Null);
        await File.WriteAllBytesAsync(path, previous);

        LightClientStore resumed = (await Journal(ConsensusTests.Spec, checkpoint).LoadAsync(61, CancellationToken.None))!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(resumed.FinalizedHeader.Beacon!.Slot, Is.EqualTo(6));
            Assert.That(resumed.OptimisticHeader.Beacon!.Slot, Is.EqualTo(60));
            Assert.That(resumed.BestUpdate, Is.Null);
            Assert.That(resumed.ForceUpdate(8200), Is.False);
            Assert.That(_logger.Entries, Has.Count.EqualTo(1));
            Assert.That(_logger.Entries[0].Message, Does.Contain(path));
        }
    }

    [Test]
    public async Task A_candidate_cannot_survive_an_interrupted_optimistic_generation_change()
    {
        (LightClientStore store, VerifiedConsensusJournal journal, Hash256 checkpoint) = await JournalWithWeakCandidateAsync();
        string path = Directory.GetFiles(_directory, "*.best").Single();
        byte[] previous = await File.ReadAllBytesAsync(path);
        LightClientOptimisticUpdate later = AsOptimistic(ConsensusTests.Update(1, 200, 201, 1, participants: 100));
        store.Process(later, 201);
        await journal.AppendAsync(later, store, CancellationToken.None);
        await File.WriteAllBytesAsync(path, previous);

        LightClientStore resumed = (await Journal(ConsensusTests.Spec, checkpoint).LoadAsync(8194, CancellationToken.None))!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(resumed.OptimisticHeader.Beacon!.Slot, Is.EqualTo(200));
            Assert.That(resumed.BestUpdate, Is.Null);
            Assert.That(resumed.ForceUpdate(8194), Is.False);
        }
    }

    [Test]
    public async Task Candidate_signature_and_committee_proof_are_reverified_even_with_a_valid_checksum(
        [Values] bool signature)
    {
        (LightClientStore _, VerifiedConsensusJournal _, Hash256 checkpoint) = await JournalWithWeakCandidateAsync();
        string path = Directory.GetFiles(_directory, "*.best").Single();
        byte[] file = await File.ReadAllBytesAsync(path);
        LightClientUpdate candidate = LightClientWireCodec.DecodeUpdate(file.AsSpan(140, file.Length - 172), ConsensusTests.Spec);
        if (signature) candidate.SyncAggregate!.SyncCommitteeSignature = default;
        else candidate.NextSyncCommitteeBranch![0] = Keccak.Compute("invalid recovery proof");
        LightClientWireCodec.EncodeUpdate(candidate, ConsensusTests.Spec).CopyTo(file, 140);
        SHA256.HashData(file.AsSpan(0, file.Length - 32), file.AsSpan(file.Length - 32));
        await File.WriteAllBytesAsync(path, file);

        LightClientStore resumed = (await Journal(ConsensusTests.Spec, checkpoint).LoadAsync(8194, CancellationToken.None))!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(resumed.BestUpdate, Is.Null);
            Assert.That(resumed.NextSyncCommitteeKnown, Is.False);
            Assert.That(resumed.OptimisticHeader.Beacon!.Slot, Is.EqualTo(100));
            Assert.That(_logger.Entries, Has.Count.EqualTo(1));
            Assert.That(_logger.Entries[0].Exception, Is.TypeOf<InvalidDataException>());
        }
    }

    [Test]
    public async Task Candidate_replacements_remain_bounded_without_growing_the_committee_chain()
    {
        (LightClientStore store, VerifiedConsensusJournal journal, Hash256 checkpoint) = await JournalWithWeakCandidateAsync();
        string chainPath = Directory.GetFiles(_directory, "*.chain").Single();
        byte[] chain = await File.ReadAllBytesAsync(chainPath);
        for (ulong attested = 49; attested >= 40; attested--)
        {
            LightClientUpdate candidate = ConsensusTests.Update(2, attested, attested + 1, 1, participants: 100, nextKey: 2);
            store.Process(candidate, 101);
            await journal.AppendAsync(candidate, store, CancellationToken.None);
        }

        LightClientStore resumed = (await Journal(ConsensusTests.Spec, checkpoint).LoadAsync(101, CancellationToken.None))!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await File.ReadAllBytesAsync(chainPath), Is.EqualTo(chain));
            Assert.That(resumed.BestUpdate!.AttestedHeader!.Beacon!.Slot, Is.EqualTo(40));
            Assert.That(new FileInfo(Directory.GetFiles(_directory, "*.best").Single()).Length, Is.LessThanOrEqualTo(256 * 1024 + 172));
        }
    }

    [Test]
    public async Task A_saved_candidate_is_folded_into_the_chain_before_timeout_recovery()
    {
        (LightClientStore _, VerifiedConsensusJournal _, Hash256 checkpoint) = await JournalWithWeakCandidateAsync();
        VerifiedConsensusJournal journal = Journal(ConsensusTests.Spec, checkpoint);
        LightClientStore resumed = (await journal.LoadAsync(8194, CancellationToken.None))!;
        LightClientUpdate candidate = resumed.BestUpdate!;
        Assert.That(resumed.ForceUpdate(8194), Is.True);
        await journal.AppendForcedAsync(candidate, 8194, resumed, CancellationToken.None);

        LightClientStore recovered = (await Journal(ConsensusTests.Spec, checkpoint).LoadAsync(8194, CancellationToken.None))!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(recovered.NextSyncCommitteeKnown, Is.True);
            Assert.That(recovered.FinalizedHeader.Beacon!.Slot, Is.EqualTo(1));
            Assert.That(recovered.OptimisticHeader.Beacon!.Slot, Is.EqualTo(100));
            Assert.That(recovered.BestUpdate, Is.Null);
            Assert.That(_logger.Entries, Is.Empty);
        }
    }

    private async Task<(LightClientStore Store, VerifiedConsensusJournal Journal, Hash256 Checkpoint)> JournalWithWeakCandidateAsync()
    {
        LightClientBootstrap bootstrap = ConsensusTests.Bootstrap(1, 1);
        Hash256 checkpoint = SszRoots.HashTreeRoot(bootstrap.Header!.Beacon!);
        LightClientStore store = new(ConsensusTests.Spec, checkpoint, bootstrap, 1);
        VerifiedConsensusJournal journal = Journal(ConsensusTests.Spec, checkpoint);
        await journal.InitializeAsync(bootstrap, store, CancellationToken.None);
        LightClientOptimisticUpdate optimistic = AsOptimistic(ConsensusTests.Update(1, 100, 101, 1, participants: 100));
        store.Process(optimistic, 101);
        await journal.AppendAsync(optimistic, store, CancellationToken.None);
        LightClientUpdate candidate = ConsensusTests.Update(2, 50, 51, 1, participants: 100, nextKey: 2);
        store.Process(candidate, 101);
        await journal.AppendAsync(candidate, store, CancellationToken.None);
        return (store, journal, checkpoint);
    }

    private VerifiedConsensusJournal Journal(BeaconChainSpec spec, Hash256 checkpoint, string network = "mainnet") =>
        new(_directory, spec, network, checkpoint, _logger);

    private async Task<(LightClientStore Store, VerifiedConsensusJournal Journal, Hash256 Checkpoint)> JournalWithOptionalFinalizedHeadAsync()
    {
        LightClientBootstrap bootstrap = ConsensusTests.Bootstrap(1, 1);
        Hash256 checkpoint = SszRoots.HashTreeRoot(bootstrap.Header!.Beacon!);
        LightClientStore store = new(ConsensusTests.Spec, checkpoint, bootstrap, 1);
        VerifiedConsensusJournal journal = Journal(ConsensusTests.Spec, checkpoint);
        await journal.InitializeAsync(bootstrap, store, CancellationToken.None);
        LightClientUpdate peak = ConsensusTests.Update(2, 3, 4, 1);
        store.Process(peak, 4);
        await journal.AppendAsync(peak, store, CancellationToken.None);
        LightClientUpdate head = ConsensusTests.Update(4, 5, 6, 1);
        store.Process(head, 6);
        await journal.AppendAsync(head, store, CancellationToken.None);
        return (store, journal, checkpoint);
    }

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

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
