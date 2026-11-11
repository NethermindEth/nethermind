// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.NodeBuffer;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

/// <summary>
/// Slot reads at the states inside a compacted persisted range, for slots written before a storage-clearing self-destruct
/// later in that range.
/// </summary>
/// <remarks>
/// A compacted snapshot holds what the last state of its range holds, so its merge drops such slots, and the bloom built
/// with it or from it has no key for them. The narrower snapshots inside the range still hold them and are what a read at
/// a state before the clear walks. Every read goes through <see cref="FlatDbManager.GatherReadOnlySnapshotBundle"/> and
/// <see cref="ReadOnlySnapshotBundle.GetSlot(Address, in UInt256, int, out UInt256?)"/>, as <c>FlatStateReader</c> reads a
/// slot. RocksDB is a substitute at block 0 holding <see cref="StaleValue"/> in every slot the tests read.
/// </remarks>
[TestFixture]
[Parallelizable(ParallelScope.All)]
public class PersistedSnapshotSharedBloomTests
{
    // Enough slots that a bloom false positive on a few of them cannot hide the result for the rest.
    private const int SlotCount = 64;

    private static readonly Address Contract = TestItem.AddressA;
    private static readonly UInt256 StaleValue = 0xDEAD;

    private static UInt256 Slot(int i) => (UInt256)(1000 + i);

    private static UInt256 Value(int i) => (UInt256)(0x100 + i);

    private static StateId State(ulong block) => new(block, Keccak.Compute($"{block}"));

    // CompactSize 4 from block 0: blocks 2 and 6 end a two-block merge, block 4 a CompactSized one, and block 8 both a
    // CompactSized one and the large (0, 8] merge.
    private static FlatDbConfig Config() => new() { CompactSize = 4, CompactionOffset = 0 };

    /// <param name="Written">The block that writes every slot.</param>
    /// <param name="Cleared">The block whose self-destruct clears the contract's storage.</param>
    /// <param name="Recreated">Whether the contract was cleared in the block before <paramref name="Written"/> too, so
    /// the snapshot of <paramref name="Written"/> carries the "new" self-destruct flag of the re-created contract.</param>
    public readonly record struct Scenario(ulong Written, ulong Cleared, bool Recreated)
    {
        public override string ToString() =>
            $"{SlotCount} slots written in block {Written}{(Recreated ? $" by the contract re-created after a clear in block {Written - 1}" : "")}, storage cleared in block {Cleared}";
    }

    /// <summary>
    /// A large merge shares its bloom with every persisted snapshot inside its range
    /// (<see cref="PersistedSnapshotCompactor"/> → <see cref="ISnapshotRepository.ShareBloomAcrossRange"/>).
    /// </summary>
    /// <remarks>
    /// Cleared in block 6, the slots are written in the first CompactSize window and cleared in the second, so the (0, 8]
    /// merge is the only compacted snapshot that drops them; the CompactSized snapshot at 8 shares its bloom over (4, 8]
    /// only. Cleared in block 3, the CompactSized (0, 4] merge has dropped them already, so the (0, 8] merge never sees
    /// them: what it drops itself is not all its bloom misses.
    /// </remarks>
    [TestCase(2ul, 6ul, false)]
    [TestCase(2ul, 6ul, true)]
    [TestCase(2ul, 3ul, false)]
    [TestCase(2ul, 3ul, true)]
    public async Task Large_merge_keeps_slots_cleared_later_in_its_range_readable_at_the_states_before_the_clear(ulong written, ulong cleared, bool recreated)
    {
        Scenario scenario = new(written, cleared, recreated);
        using FlatTestContainer tier = new(Config());
        for (ulong block = 1; block <= 8; block++) ConvertAndCompact(tier, block, scenario, largeMerge: true);

        await using FlatDbManager manager = StartManager(tier);
        AssertReads(manager, scenario, lastBlock: 8);
    }

    /// <summary>
    /// The CompactSized snapshot made at a large boundary shares its bloom with the persisted snapshots inside its range
    /// as well: <see cref="PersistedSnapshotCompactor"/> shares whenever the boundary block is a large one, whatever the
    /// tier of the snapshot it just made.
    /// </summary>
    /// <remarks>
    /// The large merge does not run here. On a node it runs later on another worker, and not at all when the window,
    /// clamped to the persisted block, holds only the CompactSized snapshot.
    /// </remarks>
    [TestCase(false)]
    [TestCase(true)]
    public async Task CompactSized_merge_at_a_large_boundary_keeps_slots_cleared_later_in_its_range_readable(bool recreated)
    {
        Scenario scenario = new(Written: 5, Cleared: 7, recreated);
        using FlatTestContainer tier = new(Config());
        for (ulong block = 1; block <= 8; block++) ConvertAndCompact(tier, block, scenario, largeMerge: false);

        await using FlatDbManager manager = StartManager(tier);
        AssertReads(manager, scenario, lastBlock: 8);
    }

    /// <summary>
    /// After a restart, <see cref="PersistedSnapshotLoader"/> builds one bloom per snapshot of the widest chain from that
    /// snapshot's own content and shares it with every persisted snapshot inside its range.
    /// </summary>
    /// <remarks>
    /// The first run leaves the CompactSized (0, 4] and the two-block (4, 6] merges over their one-block snapshots, as
    /// compaction does while finality lags. No large boundary is reached, so nothing is shared before the restart and the
    /// same reads are right there. The widest chain after the restart is [(0, 4], (4, 6]]. The slots are written and
    /// cleared either inside (0, 4] or inside (4, 6].
    /// </remarks>
    [TestCase(2ul, 4ul, false)]
    [TestCase(2ul, 4ul, true)]
    [TestCase(5ul, 6ul, false)]
    [TestCase(5ul, 6ul, true)]
    public async Task Reload_keeps_slots_cleared_later_in_the_widest_range_readable(ulong written, ulong cleared, bool recreated)
    {
        Scenario scenario = new(written, cleared, recreated);
        using TempPath dir = TempPath.GetTempDirectory();
        MemDb catalog = new();
        using (FlatTestContainer run = new(Config(), baseDbPath: dir.Path, catalogDb: catalog))
        {
            for (ulong block = 1; block <= 6; block++) ConvertAndCompact(run, block, scenario, largeMerge: true);

            await using FlatDbManager before = StartManager(run);
            AssertReads(before, scenario, lastBlock: 6, " before the restart");
        }

        // The restart: building the container loads the catalog, then rebuilds and shares the blooms.
        using FlatTestContainer restarted = new(Config(), baseDbPath: dir.Path, catalogDb: catalog);
        await using FlatDbManager manager = StartManager(restarted);
        AssertReads(manager, scenario, lastBlock: 6, " after the restart");
    }

    // Converts each block as PersistenceManager does and compacts it as PersistedSnapshotCompactor.ProcessCompactBatch
    // does: the two-block merges, then the CompactSized one, then the large merge.
    private static void ConvertAndCompact(FlatTestContainer tier, ulong block, Scenario scenario, bool largeMerge)
    {
        using (Snapshot snapshot = tier.ResourcePool.CreateSnapshot(State(block - 1), State(block), ResourcePool.Usage.MainBlockProcessing))
        {
            Write(snapshot.Content, block, scenario);
            tier.ConvertToPersistedBase(snapshot).Dispose();
        }

        ICompactionSchedule schedule = tier.Resolve<ICompactionSchedule>();
        bool large = schedule.IsLargeCompactionBoundary(block);
        if (!large && !schedule.IsCompactSizeBoundary(block)) tier.Compactor.DoCompactSnapshot(State(block));
        if (large || schedule.IsCompactSizeBoundary(block)) tier.Compactor.DoCompactCompactSized(State(block));
        if (large && largeMerge) tier.Compactor.DoCompactSnapshot(State(block));
    }

    private static void Write(SnapshotContent content, ulong block, Scenario scenario)
    {
        // Every block writes something else too, so no snapshot is empty.
        content.Accounts[new Address(Keccak.Compute($"filler {block}"))] = new Account(block, block);

        if (block == scenario.Cleared || (scenario.Recreated && block == scenario.Written - 1))
        {
            // A self-destruct of a contract that had storage: the barrier a merge drops the older slots at.
            content.Accounts[Contract] = null;
            content.SelfDestructedStorageAddresses[Contract] = false;
        }
        else if (block == scenario.Written)
        {
            content.Accounts[Contract] = new Account(1, block);
            // A contract created where the storage is empty.
            if (scenario.Recreated) content.SelfDestructedStorageAddresses[Contract] = true;
            for (int i = 0; i < SlotCount; i++) content.Storages[(Contract, Slot(i))] = Value(i);
        }
    }

    private static UInt256?[] Expected(Scenario scenario, ulong block)
    {
        UInt256?[] expected = new UInt256?[SlotCount];
        ulong firstChange = scenario.Recreated ? scenario.Written - 1 : scenario.Written;
        for (int i = 0; i < SlotCount; i++)
        {
            expected[i] = block < firstChange ? StaleValue
                : block >= scenario.Written && block < scenario.Cleared ? Value(i)
                : null;
        }

        return expected;
    }

    private static void AssertReads(FlatDbManager manager, Scenario scenario, ulong lastBlock, string when = "")
    {
        using (Assert.EnterMultipleScope())
        {
            for (ulong block = 1; block <= lastBlock; block++)
            {
                UInt256?[] expected = Expected(scenario, block);
                UInt256?[] actual = new UInt256?[SlotCount];
                int wrong = 0;
                for (int i = 0; i < SlotCount; i++)
                {
                    actual[i] = ReadSlot(manager, State(block), Slot(i));
                    if (actual[i] != expected[i]) wrong++;
                }

                Assert.That(actual, Is.EqualTo(expected),
                    $"{wrong} of the contract's {SlotCount} slots read wrong at block {block}{when}; {scenario}; RocksDB holds {StaleValue}");
            }
        }
    }

    // As FlatStateReader reads a slot.
    private static UInt256? ReadSlot(FlatDbManager manager, StateId at, UInt256 index)
    {
        using ReadOnlySnapshotBundle bundle = manager.GatherReadOnlySnapshotBundle(at);
        bundle.GetSlot(Contract, index, bundle.DetermineSelfDestructSnapshotIdx(Contract), out UInt256? value);
        return value;
    }

    // A manager over the container's persisted tier, with RocksDB a substitute at block 0. The container has already
    // loaded the tier, so the manager's own load is a substitute.
    private static FlatDbManager StartManager(FlatTestContainer tier)
    {
        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        reader.CurrentState.Returns(State(0));
        for (int i = 0; i < SlotCount; i++)
        {
            reader.TryGetSlot(Contract, Slot(i), ref Arg.Any<UInt256>()).Returns(static call =>
            {
                call[2] = StaleValue;
                return true;
            });
        }

        IPersistenceManager persistenceManager = Substitute.For<IPersistenceManager>();
        persistenceManager.LeaseReader(Arg.Any<ReaderFlags>()).Returns(reader);
        persistenceManager.GetCurrentPersistedStateId().Returns(State(0));
        IProcessExitSource processExit = Substitute.For<IProcessExitSource>();
        processExit.Token.Returns(CancellationToken.None);

        return new FlatDbManager(
            Substitute.For<IResourcePool>(),
            processExit,
            tier.Resolve<INodeRecordBuffer>(),
            Substitute.For<ISnapshotCompactor>(),
            tier.Repository,
            persistenceManager,
            Substitute.For<IPersistedSnapshotLoader>(),
            tier.Config,
            new BlocksConfig(),
            LimboLogs.Instance,
            enableDetailedMetrics: false);
    }
}
