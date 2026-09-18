// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Trie.Pruning;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.Test;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

[TestFixture]
public class CaptureUnderLongFinalityTests
{
    [Test]
    public async Task Capture_follows_a_sync_that_persists_through_the_backstop()
    {
        FlatDbConfig config = new()
        {
            HistoryEnabled = true,
            EnableLongFinality = true,
            CompactSize = 16,
            CompactionOffset = 0,
            MinReorgDepth = 8,
            MaxInMemoryBaseSnapshotCount = 16,
            MaxReorgDepth = 32,
            LongFinalityMaxReorgDepth = 32,
            InlineCompaction = true
        };

        using SnapshotableMemColumnsDb<FlatDbColumns> flatColumns = new();
        using SnapshotableMemColumnsDb<FlatHistoryColumns> historyColumns = new();
        using FlatTestContainer tier = new(config);
        SnapshotRepository repository = tier.Repository;
        ResourcePool pool = tier.ResourcePool;

        (HistoryAvailability availability, HistoryRowFormat rowFormat) = HistoryColumnsWriter.CreateSharedFormat(historyColumns, config);
        HistoryWriter writer = new(flatColumns, historyColumns, config, availability, rowFormat, LimboLogs.Instance, commitments: null);

        IPersistence persistence = Substitute.For<IPersistence>();
        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        StateId genesis = new(0, Keccak.EmptyTreeHash);
        reader.CurrentState.Returns(genesis);
        persistence.CreateReader().Returns(reader);
        persistence.CreateWriteBatch(Arg.Any<StateId>(), Arg.Any<StateId>()).Returns(Substitute.For<IPersistence.IWriteBatch>());

        using PersistenceManager manager = new(
            config,
            tier.Resolve<ICompactionSchedule>(),
            Substitute.For<IFinalizedStateProvider>(),
            persistence,
            repository,
            NullStatePersistenceBarrier.Instance,
            LimboLogs.Instance,
            tier.Compactor,
            tier.Loader,
            Substitute.For<IProcessExitSource>(),
            writer);

        bool captureDisabled = false;
        writer.CaptureDisabled += () => captureDisabled = true;
        writer.SeedGenesis([new System.Collections.Generic.KeyValuePair<Address, Account>(TestItem.AddressA, new Account(0, 1))], genesis.StateRoot);

        StateId previous = genesis;
        for (ulong block = 1; block <= 200; block++)
        {
            byte[] rootBytes = new byte[32];
            rootBytes[0] = (byte)(block & 0xff);
            rootBytes[1] = (byte)((block >> 8) & 0xff);
            StateId next = new(block, new ValueHash256(rootBytes));

            Snapshot snapshot = pool.CreateSnapshot(previous, next, ResourcePool.Usage.MainBlockProcessing);
            snapshot.Content.Accounts[TestItem.AddressA] = new Account(block, (UInt256)(block * 100));
            repository.AddStateId(next);
            repository.TryAdd(snapshot, SnapshotTier.InMemoryBase);

            await manager.AddToPersistence(next);

            Assert.That(captureDisabled, Is.False, $"capture was disabled while persisting block {block}; watermark {writer.LastCapturedBlock}");
            previous = next;
        }

        Assert.That(writer.LastCapturedBlock, Is.GreaterThan(100ul), "the watermark must follow the sync");
        await tier.Compactor.DisposeAsync();
    }

    // The consensus client keeps driving the tip while the node is still syncing from genesis: the engine
    // commits a state thousands of blocks above the sync position, with a parent this node has never seen.
    [Test]
    public async Task Capture_survives_a_tip_state_committed_while_the_sync_is_far_below()
    {
        FlatDbConfig config = new()
        {
            HistoryEnabled = true,
            EnableLongFinality = true,
            CompactSize = 16,
            CompactionOffset = 0,
            MinReorgDepth = 8,
            MaxInMemoryBaseSnapshotCount = 16,
            MaxReorgDepth = 32,
            LongFinalityMaxReorgDepth = 32,
            InlineCompaction = true
        };

        using SnapshotableMemColumnsDb<FlatDbColumns> flatColumns = new();
        using SnapshotableMemColumnsDb<FlatHistoryColumns> historyColumns = new();
        using FlatTestContainer tier = new(config);
        SnapshotRepository repository = tier.Repository;
        ResourcePool pool = tier.ResourcePool;

        (HistoryAvailability availability, HistoryRowFormat rowFormat) = HistoryColumnsWriter.CreateSharedFormat(historyColumns, config);
        HistoryWriter writer = new(flatColumns, historyColumns, config, availability, rowFormat, LimboLogs.Instance, commitments: null);

        IPersistence persistence = Substitute.For<IPersistence>();
        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        StateId genesis = new(0, Keccak.EmptyTreeHash);
        reader.CurrentState.Returns(genesis);
        persistence.CreateReader().Returns(reader);
        persistence.CreateWriteBatch(Arg.Any<StateId>(), Arg.Any<StateId>()).Returns(Substitute.For<IPersistence.IWriteBatch>());

        using PersistenceManager manager = new(
            config,
            tier.Resolve<ICompactionSchedule>(),
            Substitute.For<IFinalizedStateProvider>(),
            persistence,
            repository,
            NullStatePersistenceBarrier.Instance,
            LimboLogs.Instance,
            tier.Compactor,
            tier.Loader,
            Substitute.For<IProcessExitSource>(),
            writer);

        bool captureDisabled = false;
        writer.CaptureDisabled += () => captureDisabled = true;
        writer.SeedGenesis([new System.Collections.Generic.KeyValuePair<Address, Account>(TestItem.AddressA, new Account(0, 1))], genesis.StateRoot);

        StateId previous = genesis;
        for (ulong block = 1; block <= 120; block++)
        {
            byte[] rootBytes = new byte[32];
            rootBytes[0] = (byte)(block & 0xff);
            rootBytes[1] = (byte)((block >> 8) & 0xff);
            StateId next = new(block, new ValueHash256(rootBytes));

            Snapshot snapshot = pool.CreateSnapshot(previous, next, ResourcePool.Usage.MainBlockProcessing);
            snapshot.Content.Accounts[TestItem.AddressA] = new Account(block, (UInt256)(block * 100));
            repository.AddStateId(next);
            repository.TryAdd(snapshot, SnapshotTier.InMemoryBase);
            await manager.AddToPersistence(next);
            previous = next;

            if (block == 60)
            {
                byte[] tipParentBytes = new byte[32];
                tipParentBytes[0] = 0xAA;
                byte[] tipBytes = new byte[32];
                tipBytes[0] = 0xBB;
                StateId tipParent = new(100_000, new ValueHash256(tipParentBytes));
                StateId tip = new(100_001, new ValueHash256(tipBytes));

                Snapshot tipSnapshot = pool.CreateSnapshot(tipParent, tip, ResourcePool.Usage.MainBlockProcessing);
                tipSnapshot.Content.Accounts[TestItem.AddressB] = new Account(1, 1);
                repository.AddStateId(tip);
                repository.TryAdd(tipSnapshot, SnapshotTier.InMemoryBase);
                await manager.AddToPersistence(tip);
            }

            Assert.That(captureDisabled, Is.False, $"capture was disabled at block {block}; watermark {writer.LastCapturedBlock}");
        }

        Assert.That(writer.LastCapturedBlock, Is.GreaterThan(60ul), "the watermark must keep following the sync after the tip state");
        await tier.Compactor.DisposeAsync();
    }

    /// <summary>The node's own defaults, and the finalized view a syncing node actually gets: the consensus client
    /// reports a finalized block at the tip while this node's canonical roots exist only up to its sync position.</summary>
    private sealed class SyncingChainFinalizedStateProvider(System.Collections.Generic.IReadOnlyDictionary<ulong, Hash256> canonicalRoots) : IFinalizedStateProvider
    {
        public ulong FinalizedBlockNumber => 26_000_000;

        public Hash256? GetFinalizedStateRootAt(ulong blockNumber) =>
            canonicalRoots.TryGetValue(blockNumber, out Hash256? root) ? root : null;
    }

    [Test]
    public async Task Capture_follows_a_genesis_sync_under_production_defaults_with_a_tip_state()
    {
        FlatDbConfig config = new() { HistoryEnabled = true, CompactionOffset = 0, InlineCompaction = true };

        using SnapshotableMemColumnsDb<FlatDbColumns> flatColumns = new();
        using SnapshotableMemColumnsDb<FlatHistoryColumns> historyColumns = new();
        System.Collections.Generic.Dictionary<ulong, Hash256> canonicalRoots = [];
        SyncingChainFinalizedStateProvider finalized = new(canonicalRoots);
        using FlatTestContainer tier = new(config, finalizedStateProvider: finalized);
        SnapshotRepository repository = tier.Repository;
        ResourcePool pool = tier.ResourcePool;

        (HistoryAvailability availability, HistoryRowFormat rowFormat) = HistoryColumnsWriter.CreateSharedFormat(historyColumns, config);
        HistoryWriter writer = new(flatColumns, historyColumns, config, availability, rowFormat, LimboLogs.Instance, commitments: null);

        IPersistence persistence = Substitute.For<IPersistence>();
        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        StateId genesis = new(0, Keccak.EmptyTreeHash);
        reader.CurrentState.Returns(genesis);
        persistence.CreateReader().Returns(reader);
        persistence.CreateWriteBatch(Arg.Any<StateId>(), Arg.Any<StateId>()).Returns(Substitute.For<IPersistence.IWriteBatch>());

        using PersistenceManager manager = new(
            config,
            tier.Resolve<ICompactionSchedule>(),
            finalized,
            persistence,
            repository,
            NullStatePersistenceBarrier.Instance,
            LimboLogs.Instance,
            tier.Compactor,
            tier.Loader,
            Substitute.For<IProcessExitSource>(),
            writer);

        bool captureDisabled = false;
        writer.CaptureDisabled += () => captureDisabled = true;
        writer.SeedGenesis([new System.Collections.Generic.KeyValuePair<Address, Account>(TestItem.AddressA, new Account(0, 1))], genesis.StateRoot);

        StateId previous = genesis;
        for (ulong block = 1; block <= 800; block++)
        {
            byte[] rootBytes = new byte[32];
            rootBytes[0] = (byte)(block & 0xff);
            rootBytes[1] = (byte)((block >> 8) & 0xff);
            StateId next = new(block, new ValueHash256(rootBytes));
            canonicalRoots[block] = new Hash256(rootBytes);

            Snapshot snapshot = pool.CreateSnapshot(previous, next, ResourcePool.Usage.MainBlockProcessing);
            snapshot.Content.Accounts[TestItem.AddressA] = new Account(block, (UInt256)(block * 100));
            repository.AddStateId(next);
            repository.TryAdd(snapshot, SnapshotTier.InMemoryBase);
            repository.SetLastCommittedStateId(next);
            await manager.AddToPersistence(next);
            previous = next;

            // The engine keeps driving the tip: it commits a state whose parent this node has never seen.
            if (block % 100 == 0)
            {
                byte[] tipParentBytes = new byte[32];
                tipParentBytes[0] = 0xAA;
                tipParentBytes[2] = (byte)block;
                byte[] tipBytes = new byte[32];
                tipBytes[0] = 0xBB;
                tipBytes[2] = (byte)block;
                StateId tipParent = new(26_000_000, new ValueHash256(tipParentBytes));
                StateId tip = new(26_000_001, new ValueHash256(tipBytes));

                Snapshot tipSnapshot = pool.CreateSnapshot(tipParent, tip, ResourcePool.Usage.MainBlockProcessing);
                tipSnapshot.Content.Accounts[TestItem.AddressB] = new Account(1, 1);
                repository.AddStateId(tip);
                repository.TryAdd(tipSnapshot, SnapshotTier.InMemoryBase);
                repository.SetLastCommittedStateId(tip);
                await manager.AddToPersistence(tip);
            }

            Assert.That(captureDisabled, Is.False, $"capture was disabled at block {block}; watermark {writer.LastCapturedBlock}");
        }

        Assert.That(writer.LastCapturedBlock, Is.GreaterThan(600ul), "the watermark must follow the sync");
        await tier.Compactor.DisposeAsync();
    }
}
