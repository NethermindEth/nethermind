// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.NodeBuffer;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
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

    // A block no scenario reaches: the event never happens.
    private const ulong Never = ulong.MaxValue;

    private static readonly Address Contract = TestItem.AddressA;
    // Sorts after the contract, so a merge always meets another address's group, with a "new" record, after the contract's.
    private static readonly Address Filler = new("0xffffffffffffffffffffffffffffffffffffffff");
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
        /// <summary>A block that creates the contract where the storage is empty (a "new" self-destruct record); 0 for none.</summary>
        public ulong Created { get; init; }

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

    /// <summary>The snapshot whose merged or rebuilt bloom a mechanism case inspects.</summary>
    public enum Donor { Large, CompactSized, ReloadedCompactSized, ReloadedLarge }

    /// <summary>
    /// A merged or rebuilt bloom is shared over its range only when the range holds no storage clear (a "destructed"
    /// self-destruct record); otherwise it stays on its own snapshot, which still gets a real bloom, at reload too.
    /// </summary>
    /// <remarks>
    /// <paramref name="written"/> <see cref="Never"/>: a "destructed" record with no slot to drop, then a "new" one of
    /// the same contract, must still keep the bloom: the record decides, not a drop in this merge or the newest flag.
    /// <paramref name="cleared"/> <see cref="Never"/>: "new" records alone still share, at runtime and after a restart.
    /// The reloaded (0, 4] case also checks that the clear-free (4, 6] next to it shares.
    /// </remarks>
    [TestCase(Donor.Large, 2ul, 6ul, 0ul, false)]
    [TestCase(Donor.CompactSized, 5ul, 7ul, 0ul, false)]
    [TestCase(Donor.ReloadedCompactSized, 2ul, 4ul, 0ul, false)]
    [TestCase(Donor.Large, Never, 3ul, 7ul, false)]
    [TestCase(Donor.CompactSized, Never, 5ul, 7ul, false)]
    [TestCase(Donor.ReloadedLarge, Never, 3ul, 7ul, false)]
    [TestCase(Donor.Large, 2ul, Never, 2ul, true)]
    [TestCase(Donor.ReloadedLarge, 2ul, Never, 2ul, true)]
    public void Merged_bloom_is_shared_only_over_a_range_without_a_storage_clear(Donor donor, ulong written, ulong cleared, ulong created, bool shares)
    {
        Scenario scenario = new(written, cleared, Recreated: false) { Created = created };
        bool reload = donor is Donor.ReloadedCompactSized or Donor.ReloadedLarge;
        using TempPath dir = TempPath.GetTempDirectory();
        MemDb catalog = new();
        using (FlatTestContainer run = new(Config(), baseDbPath: dir.Path, catalogDb: catalog))
        {
            for (ulong block = 1; block <= (donor == Donor.ReloadedCompactSized ? 6ul : 8ul); block++)
                ConvertAndCompact(run, block, scenario, largeMerge: donor != Donor.CompactSized);
            if (!reload) AssertMechanism(run.Repository, donor, shares);
        }

        if (!reload) return;
        // After the loader has returned: a bloom it released too early fails the owner's probes.
        using FlatTestContainer restarted = new(Config(), baseDbPath: dir.Path, catalogDb: catalog);
        AssertMechanism(restarted.Repository, donor, shares);
    }

    /// <summary>
    /// A large merge over a mixed chain whose two-block source already dropped the slots: the (0, 8] merge drops nothing
    /// itself, yet base 5, which holds them, must not adopt its bloom.
    /// </summary>
    [Test]
    public async Task Large_merge_over_a_two_block_merge_that_dropped_the_slots_keeps_them_readable()
    {
        Scenario scenario = new(Written: 5, Cleared: 6, Recreated: false);
        using FlatTestContainer tier = new(Config());
        // No CompactSized merge at 8, so the large merge assembles [(0, 4], (4, 6], 7, 8].
        for (ulong block = 1; block <= 8; block++) ConvertAndCompact(tier, block, scenario, largeMerge: false, compactSizedAtLargeBoundary: false);
        using (PersistedSnapshotList sources = tier.Repository.AssemblePersistedSnapshotsForCompaction(State(8), 0))
        {
            Assert.That(sources.Select(static s => (s.Tier, s.From.BlockNumber, s.To.BlockNumber)), Is.EqualTo(new[]
            {
                (SnapshotTier.PersistedCompactSized, 0ul, 4ul), (SnapshotTier.PersistedSmallCompacted, 4ul, 6ul),
                (SnapshotTier.PersistedBase, 6ul, 7ul), (SnapshotTier.PersistedBase, 7ul, 8ul),
            }), "precondition: the large merge's sources");
        }

        tier.Compactor.DoCompactSnapshot(State(8));

        await using FlatDbManager manager = StartManager(tier);
        using (Assert.EnterMultipleScope())
        {
            AssertReads(manager, scenario, lastBlock: 8);
            AssertSharing(tier.Repository, State(8), SnapshotTier.PersistedLargeCompacted, shares: false);
            AssertEveryBloomCoversItsTable(tier.Repository);
        }
    }

    /// <summary>
    /// A restart after compaction crossed a large boundary: before it the clear-free CompactSized (4, 8] shared its bloom
    /// and the (0, 8] merge, whose range holds the clear, did not. After it the widest (0, 8] keeps its rebuilt bloom to
    /// itself, and the catalog is not rewritten.
    /// </summary>
    [Test]
    public async Task Restart_after_a_large_merge_keeps_slots_cleared_later_in_its_range_readable()
    {
        Scenario scenario = new(Written: 2, Cleared: 3, Recreated: false);
        using TempPath dir = TempPath.GetTempDirectory();
        MemDb catalog = new();
        CatalogEntry[] before;
        using (FlatTestContainer run = new(Config(), baseDbPath: dir.Path, catalogDb: catalog))
        {
            for (ulong block = 1; block <= 8; block++) ConvertAndCompact(run, block, scenario, largeMerge: true);
            AssertSharing(run.Repository, State(8), SnapshotTier.PersistedCompactSized, shares: true);
            before = [.. run.Resolve<ISnapshotCatalog>().Load()];
        }

        using FlatTestContainer restarted = new(Config(), baseDbPath: dir.Path, catalogDb: catalog);
        await using FlatDbManager manager = StartManager(restarted);
        using (Assert.EnterMultipleScope())
        {
            AssertReads(manager, scenario, lastBlock: 8, " after the restart");
            // After the loader has returned: a bloom it released too early fails the owner's probes.
            AssertSharing(restarted.Repository, State(8), SnapshotTier.PersistedLargeCompacted, shares: false);
            Assert.That(restarted.Resolve<ISnapshotCatalog>().Load(), Is.EqualTo(before), "catalog entries, locations and tiers");
            AssertEveryBloomCoversItsTable(restarted.Repository);
        }
    }

    private static void AssertMechanism(SnapshotRepository repository, Donor donor, bool shares)
    {
        (ulong to, SnapshotTier tier) = donor switch
        {
            Donor.Large or Donor.ReloadedLarge => (8ul, SnapshotTier.PersistedLargeCompacted),
            Donor.CompactSized => (8ul, SnapshotTier.PersistedCompactSized),
            _ => (4ul, SnapshotTier.PersistedCompactSized),
        };
        using (Assert.EnterMultipleScope())
        {
            AssertSharing(repository, State(to), tier, shares);
            if (donor == Donor.ReloadedCompactSized) AssertSharing(repository, State(6), SnapshotTier.PersistedSmallCompacted, shares: true);
            AssertEveryBloomCoversItsTable(repository);
        }
    }

    // The owner has a real bloom that admits its keys, and every other persisted snapshot inside its range is on that
    // bloom exactly when it shares.
    private static void AssertSharing(SnapshotRepository repository, StateId to, SnapshotTier tier, bool shares)
    {
        bool leased = repository.TryLeasePersistedState(to, tier, out PersistedSnapshot? owner);
        Assert.That(leased, Is.True, $"precondition: {tier} at {to.BlockNumber}");
        if (!leased) return;
        using (owner)
        {
            BloomFilter bloom = owner!.Bloom;
            string range = $"{tier} ({owner.From.BlockNumber}, {to.BlockNumber}]";
            List<string> contained = [];
            using (Assert.EnterMultipleScope())
            {
                Assert.That(bloom.Count, Is.GreaterThan(0), $"{range} has a real bloom");
                Assert.That(bloom.MightContain(PersistedSnapshotBloomBuilder.AddressKey(Filler)), Is.True, $"{range} admits its keys");
                foreach (PersistedSnapshot s in Leased(repository))
                {
                    if (s.From.BlockNumber < owner.From.BlockNumber || s.To.BlockNumber > to.BlockNumber || (s.To == to && s.Tier == tier)) continue;
                    contained.Add($"{s.Tier} ({s.From.BlockNumber}, {s.To.BlockNumber}]");
                    Assert.That(ReferenceEquals(s.Bloom, bloom), Is.EqualTo(shares), $"{contained[^1]} {(shares ? "shares" : "must not share")} the bloom of {range}");
                }

                Assert.That(contained, Is.Not.Empty, $"precondition: snapshots inside {range}");
            }
        }
    }

    // Every key of every persisted snapshot's table passes that snapshot's own bloom. A placeholder passes it trivially,
    // which is why AssertSharing checks the owner's Count.
    private static void AssertEveryBloomCoversItsTable(SnapshotRepository repository)
    {
        List<string> missed = [];
        foreach (PersistedSnapshot s in Leased(repository))
        {
            using WholeReadSession session = s.BeginWholeReadSession();
            WholeReadScanner scanner = PersistedSnapshotScanner.ForWholeRead(session, s);
            List<ulong> keys = [];
            foreach (WholeReadScanner.PerAddressEntry entry in scanner.PerAddresses)
            {
                ulong addressKey = PersistedSnapshotBloomBuilder.AddressKey(entry.Address);
                keys.Add(addressKey);
                foreach (WholeReadScanner.SlotEntry slot in entry.Slots) keys.Add(PersistedSnapshotBloomBuilder.SlotKey(addressKey, slot.Slot));
            }

            foreach (WholeReadScanner.StateNodeEntry node in scanner.StateNodes) keys.Add(PersistedSnapshotBloomBuilder.StatePathKey(node.Path));
            foreach (WholeReadScanner.StorageNodeEntry node in scanner.StorageNodes) keys.Add(PersistedSnapshotBloomBuilder.StorageNodeKey(node.AddressHash, node.Path));
            int misses = keys.Count(key => !s.Bloom.MightContain(key));
            if (misses > 0) missed.Add($"{s.Tier} ({s.From.BlockNumber}, {s.To.BlockNumber}]: {misses} of {keys.Count} keys");
        }

        Assert.That(missed, Is.Empty, "keys of a snapshot's own table that its bloom rejects");
    }

    // Each persisted snapshot of the repository, leased in turn.
    private static IEnumerable<PersistedSnapshot> Leased(SnapshotRepository repository)
    {
        foreach ((StateId to, SnapshotTier tier) in repository.PersistedSnapshots.Select(static s => (s.To, s.Tier)).ToArray())
        {
            if (!repository.TryLeasePersistedState(to, tier, out PersistedSnapshot? s)) continue;
            using (s) yield return s;
        }
    }

    // Converts each block as PersistenceManager does and compacts it as PersistedSnapshotCompactor.ProcessCompactBatch
    // does: the two-block merges, then the CompactSized one, then the large merge.
    private static void ConvertAndCompact(FlatTestContainer tier, ulong block, Scenario scenario, bool largeMerge, bool compactSizedAtLargeBoundary = true)
    {
        using (Snapshot snapshot = tier.ResourcePool.CreateSnapshot(State(block - 1), State(block), ResourcePool.Usage.MainBlockProcessing))
        {
            Write(snapshot.Content, block, scenario);
            tier.ConvertToPersistedBase(snapshot).Dispose();
        }

        ICompactionSchedule schedule = tier.Resolve<ICompactionSchedule>();
        bool large = schedule.IsLargeCompactionBoundary(block);
        if (!large && !schedule.IsCompactSizeBoundary(block)) tier.Compactor.DoCompactSnapshot(State(block));
        if (large ? compactSizedAtLargeBoundary : schedule.IsCompactSizeBoundary(block)) tier.Compactor.DoCompactCompactSized(State(block));
        if (large && largeMerge) tier.Compactor.DoCompactSnapshot(State(block));
    }

    private static void Write(SnapshotContent content, ulong block, Scenario scenario)
    {
        // Every block writes something else too, so no snapshot is empty.
        content.Accounts[new Address(Keccak.Compute($"filler {block}"))] = new Account(block, block);
        content.Accounts[Filler] = new Account(block, block);
        content.SelfDestructedStorageAddresses[Filler] = true;

        if (block == scenario.Cleared || (scenario.Recreated && block == scenario.Written - 1))
        {
            // A self-destruct of a contract that had storage: the barrier a merge drops the older slots at.
            content.Accounts[Contract] = null;
            content.SelfDestructedStorageAddresses[Contract] = false;
        }
        else if (block == scenario.Written || block == scenario.Created)
        {
            content.Accounts[Contract] = new Account(1, block);
            // A contract created where the storage is empty.
            if (scenario.Recreated || block == scenario.Created) content.SelfDestructedStorageAddresses[Contract] = true;
            if (block == scenario.Written)
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
