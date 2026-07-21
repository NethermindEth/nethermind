// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Sync;
using Nethermind.State.Flat.Sync.Snap;
using Nethermind.Synchronization.FastSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Sync;

[TestFixture]
public class FlatBalHealingTests
{
    private SnapshotableMemColumnsDb<FlatDbColumns> _columnsDb = null!;
    private RocksDbPersistence _persistence = null!;
    private TrieReassembler _reassembler = null!;
    private MemDb _codeDb = null!;
    private MemDb _balDb = null!;
    private BlockAccessListStore _balStore = null!;
    private IBlockTree _blockTree = null!;
    private ITreeSyncStore _syncStore = null!;
    private FlatBalHealing _healing = null!;

    [SetUp]
    public void SetUp()
    {
        _columnsDb = new SnapshotableMemColumnsDb<FlatDbColumns>();
        _persistence = new RocksDbPersistence(_columnsDb, LimboLogs.Instance);
        _reassembler = new TrieReassembler(_persistence, LimboLogs.Instance);
        _codeDb = new MemDb();
        _balDb = new MemDb();
        _balStore = new BlockAccessListStore(_balDb);
        _blockTree = Substitute.For<IBlockTree>();
        _syncStore = Substitute.For<ITreeSyncStore>();
        _healing = new(_blockTree, _balStore, _reassembler, _persistence, _syncStore, _codeDb, LimboLogs.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _columnsDb.Dispose();
        _codeDb.Dispose();
        _balDb.Dispose();
    }

    [Test]
    public async Task Heals_when_bals_reach_target_root()
    {
        SeedInitialState(Acc(TestItem.AddressA, 100), Acc(TestItem.AddressB, 200));
        Hash256 expected = BuildRoot(Acc(TestItem.AddressA, 150), Acc(TestItem.AddressB, 200));

        BlockHeader firstPivot = Pivot(10, TestItem.KeccakA);
        BlockHeader lastPivot = SetupBlock(11, expected, BalanceBal(TestItem.AddressA, 150));

        bool result = await RunOnce(_healing, firstPivot, lastPivot, [], default);

        Assert.That(result, Is.True);
        _syncStore.Received(1).FinalizeSync(lastPivot);
    }

    [Test]
    public async Task Heals_across_multiple_chunks()
    {
        // 260 blocks crosses the 256-block chunk boundary: chunk 2 must see chunk 1's committed state.
        SeedInitialState(Acc(TestItem.AddressA, 100), Acc(TestItem.AddressB, 200));
        Hash256 expected = BuildRoot(Acc(TestItem.AddressA, 1000), Acc(TestItem.AddressB, 2000));

        BlockHeader firstPivot = Pivot(0, TestItem.KeccakA);
        BlockHeader lastPivot = firstPivot;
        for (ulong number = 1; number <= 260; number++)
        {
            ReadOnlyBlockAccessList bal = number switch
            {
                1 => BalanceBal(TestItem.AddressA, 1000),
                260 => BalanceBal(TestItem.AddressB, 2000),
                _ => EmptyBal()
            };
            lastPivot = SetupBlock(number, number == 260 ? expected : TestItem.KeccakA, bal);
        }

        bool result = await RunOnce(_healing, firstPivot, lastPivot, [], default);

        Assert.That(result, Is.True);
        _syncStore.Received(1).FinalizeSync(lastPivot);
    }

    [Test]
    public async Task Heals_when_same_account_storage_changes_across_chunks()
    {
        // slot 1 changes in chunk 1 (block 1), slot 2 changes in chunk 2 (block 300). Chunk 2 must build
        // the storage tree on chunk 1's committed storage root (read via reader.GetAccount from the flat
        // store), not the stale pre-chunk-1 root — otherwise slot 1's update is lost.
        SeedInitialState(Acc(TestItem.AddressA, 100, slots: [new Slot(1, [0x05])]));
        Hash256 expected = BuildRoot(Acc(TestItem.AddressA, 100, slots: [new Slot(1, [0x09]), new Slot(2, [0x07])]));

        BlockHeader firstPivot = Pivot(0, TestItem.KeccakA);
        BlockHeader lastPivot = firstPivot;
        for (ulong number = 1; number <= 300; number++)
        {
            ReadOnlyBlockAccessList bal = number switch
            {
                1 => StorageBal(TestItem.AddressA, 1, 9),
                300 => StorageBal(TestItem.AddressA, 2, 7),
                _ => EmptyBal()
            };
            lastPivot = SetupBlock(number, number == 300 ? expected : TestItem.KeccakA, bal);
        }

        bool result = await RunOnce(_healing, firstPivot, lastPivot, [StorageOf(TestItem.AddressA)], default);

        Assert.That(result, Is.True);
        _syncStore.Received(1).FinalizeSync(lastPivot);
    }

    [Test]
    public async Task Heals_when_bal_creates_account_with_code()
    {
        byte[] code = [0x60, 0x00, 0x60, 0x00];
        Hash256 codeHash = Keccak.Compute(code);

        SeedInitialState(Acc(TestItem.AddressA, 100));
        Hash256 expected = BuildRoot(Acc(TestItem.AddressA, 100), Acc(TestItem.AddressB, 500, code: code));

        BlockHeader firstPivot = Pivot(10, TestItem.KeccakA);
        ReadOnlyAccountChanges changes = Build.An.AccountChanges
            .WithAddress(TestItem.AddressB)
            .WithBalanceChanges(new BalanceChange(0, 500))
            .WithCodeChanges(new CodeChange(0, code))
            .TestObject;
        BlockHeader lastPivot = SetupBlock(11, expected, Bal(changes));

        bool result = await RunOnce(_healing, firstPivot, lastPivot, [], default);

        Assert.That(result, Is.True);
        Assert.That(_codeDb.Get(codeHash.Bytes), Is.EqualTo(code));
    }

    [Test]
    public async Task Heals_when_bal_updates_storage()
    {
        SeedInitialState(Acc(TestItem.AddressA, 100, slots: [new Slot(1, [0x05])]));
        Hash256 expected = BuildRoot(Acc(TestItem.AddressA, 100, slots: [new Slot(1, [0x09])]));

        BlockHeader firstPivot = Pivot(10, TestItem.KeccakA);
        ReadOnlyAccountChanges changes = Build.An.AccountChanges
            .WithAddress(TestItem.AddressA)
            .WithStorageChanges(1, new StorageChange(0, (UInt256)9))
            .TestObject;
        BlockHeader lastPivot = SetupBlock(11, expected, Bal(changes));

        bool result = await RunOnce(_healing, firstPivot, lastPivot, [StorageOf(TestItem.AddressA)], default);

        Assert.That(result, Is.True);
    }

    [Test]
    public async Task Heals_when_bal_clears_storage_slot()
    {
        SeedInitialState(Acc(TestItem.AddressA, 100, slots: [new Slot(1, [0x05])]));
        Hash256 expected = BuildRoot(Acc(TestItem.AddressA, 100));

        BlockHeader firstPivot = Pivot(10, TestItem.KeccakA);
        ReadOnlyAccountChanges changes = Build.An.AccountChanges
            .WithAddress(TestItem.AddressA)
            .WithStorageChanges(1, new StorageChange(0, UInt256.Zero))
            .TestObject;
        BlockHeader lastPivot = SetupBlock(11, expected, Bal(changes));

        bool result = await RunOnce(_healing, firstPivot, lastPivot, [StorageOf(TestItem.AddressA)], default);

        Assert.That(result, Is.True);
    }

    [Test]
    public async Task Heals_when_bal_empties_account()
    {
        SeedInitialState(Acc(TestItem.AddressA, 100), Acc(TestItem.AddressB, 200));
        Hash256 expected = BuildRoot(Acc(TestItem.AddressB, 200));

        BlockHeader firstPivot = Pivot(10, TestItem.KeccakA);
        BlockHeader lastPivot = SetupBlock(11, expected, BalanceBal(TestItem.AddressA, 0));

        bool result = await RunOnce(_healing, firstPivot, lastPivot, [], default);

        Assert.That(result, Is.True);
        _syncStore.Received(1).FinalizeSync(lastPivot);
    }

    [Test]
    public async Task Emptied_account_that_still_has_storage_is_removed_from_state()
    {
        SeedInitialState(Acc(TestItem.AddressA, 100, slots: [new Slot(1, [0x05])]), Acc(TestItem.AddressB, 200));
        Hash256 expected = BuildRoot(Acc(TestItem.AddressB, 200));

        BlockHeader firstPivot = Pivot(10, TestItem.KeccakA);
        BlockHeader lastPivot = SetupBlock(11, expected, BalanceBal(TestItem.AddressA, 0));

        bool result = await RunOnce(_healing, firstPivot, lastPivot, [StorageOf(TestItem.AddressA)], default);

        Assert.That(result, Is.True);
        _syncStore.Received(1).FinalizeSync(lastPivot);
    }

    [Test]
    public async Task Read_only_empty_account_is_preserved_during_healing()
    {
        // An empty account (nonce 0, balance 0, no code) that exists in state and appears in the BAL
        // only because it was read — not modified — must survive healing. The block did not touch it,
        // so processing it here and deleting it via the IsEmpty check drops a state trie leaf and
        // diverges from the target root. Regression for a wrong reassembled root when a BAL references
        // empty-but-present accounts as reads (e.g. touched precompiles / pre-EIP-158 empty accounts).
        SeedInitialState(Acc(TestItem.AddressA, 100), Acc(TestItem.AddressB, 0));
        Hash256 expected = BuildRoot(Acc(TestItem.AddressA, 150), Acc(TestItem.AddressB, 0));

        BlockHeader firstPivot = Pivot(10, TestItem.KeccakA);
        ReadOnlyAccountChanges changed = Build.An.AccountChanges
            .WithAddress(TestItem.AddressA)
            .WithBalanceChanges(new BalanceChange(0, 150))
            .TestObject;
        ReadOnlyAccountChanges readOnly = Build.An.AccountChanges
            .WithAddress(TestItem.AddressB)
            .TestObject;
        BlockHeader lastPivot = SetupBlock(11, expected, Bal(changed, readOnly));

        bool result = await RunOnce(_healing, firstPivot, lastPivot, [], default);

        Assert.That(result, Is.True);
        _syncStore.Received(1).FinalizeSync(lastPivot);
    }

    [Test]
    public async Task Cleared_storage_slot_is_removed_from_flat_store()
    {
        // The BAL zeroes slot 1. The trie drops it, but the flat store must drop it too — otherwise
        // a stale zero entry survives and flat diverges from trie for eth_getStorageAt / re-sync.
        SeedInitialState(Acc(TestItem.AddressA, 100, slots: [new Slot(1, [0x05]), new Slot(2, [0x07])]));
        Hash256 expected = BuildRoot(Acc(TestItem.AddressA, 100, slots: [new Slot(2, [0x07])]));

        BlockHeader firstPivot = Pivot(10, TestItem.KeccakA);
        ReadOnlyAccountChanges changes = Build.An.AccountChanges
            .WithAddress(TestItem.AddressA)
            .WithStorageChanges(1, new StorageChange(0, UInt256.Zero))
            .TestObject;
        BlockHeader lastPivot = SetupBlock(11, expected, Bal(changes));

        bool result = await RunOnce(_healing, firstPivot, lastPivot, [StorageOf(TestItem.AddressA)], default);

        Assert.That(result, Is.True);
        Assert.That(FlatSlotExists(TestItem.AddressA, 1), Is.False, "cleared slot must be removed from flat store");
        Assert.That(FlatSlotExists(TestItem.AddressA, 2), Is.True, "untouched slot must remain");
    }

    [Test]
    public async Task Removed_account_storage_is_wiped_from_flat_store()
    {
        // Account A is emptied and dropped from the trie; its flat storage must be wiped too,
        // otherwise the removed account's slots linger in the flat store.
        SeedInitialState(Acc(TestItem.AddressA, 100, slots: [new Slot(1, [0x05])]), Acc(TestItem.AddressB, 200));
        Hash256 expected = BuildRoot(Acc(TestItem.AddressB, 200));

        BlockHeader firstPivot = Pivot(10, TestItem.KeccakA);
        BlockHeader lastPivot = SetupBlock(11, expected, BalanceBal(TestItem.AddressA, 0));

        bool result = await RunOnce(_healing, firstPivot, lastPivot, [StorageOf(TestItem.AddressA)], default);

        Assert.That(result, Is.True);
        Assert.That(FlatSlotExists(TestItem.AddressA, 1), Is.False, "removed account's storage must be wiped from flat store");
    }

    [Test]
    public async Task Falls_back_when_a_bal_is_missing()
    {
        SeedInitialState(Acc(TestItem.AddressA, 100));

        BlockHeader firstPivot = Pivot(10, TestItem.KeccakA);
        BlockHeader lastPivot = SetupBlock(11, TestItem.KeccakB, bal: null);

        bool result = await RunOnce(_healing, firstPivot, lastPivot, [], default);

        Assert.That(result, Is.False);
        _syncStore.DidNotReceive().FinalizeSync(Arg.Any<BlockHeader>());
    }

    [Test]
    public async Task Falls_back_when_apply_does_not_reach_target_root()
    {
        SeedInitialState(Acc(TestItem.AddressA, 100));

        BlockHeader firstPivot = Pivot(10, TestItem.KeccakA);
        // Target root does not match what applying the BAL produces.
        BlockHeader lastPivot = SetupBlock(11, TestItem.KeccakH, BalanceBal(TestItem.AddressA, 150));

        bool result = await RunOnce(_healing, firstPivot, lastPivot, [], default);

        Assert.That(result, Is.False);
        _syncStore.DidNotReceive().FinalizeSync(Arg.Any<BlockHeader>());
    }

    [Test]
    public async Task Falls_back_when_reassembly_produces_no_root()
    {
        ITrieReassembler reassembler = Substitute.For<ITrieReassembler>();
        reassembler.TryReassemble(Arg.Any<IReadOnlyCollection<Hash256>>()).Returns((Hash256?)null);
        FlatBalHealing healing = new(_blockTree, _balStore, reassembler, _persistence, _syncStore, _codeDb, LimboLogs.Instance);

        SeedInitialState(Acc(TestItem.AddressA, 100));
        BlockHeader firstPivot = Pivot(10, TestItem.KeccakA);
        BlockHeader lastPivot = SetupBlock(11, TestItem.KeccakB, BalanceBal(TestItem.AddressA, 150));

        bool result = await RunOnce(healing, firstPivot, lastPivot, [], default);

        Assert.That(result, Is.False);
        _syncStore.DidNotReceive().FinalizeSync(Arg.Any<BlockHeader>());
    }

    [Test]
    public async Task Falls_back_when_apply_throws()
    {
        IBlockAccessListStore throwingStore = Substitute.For<IBlockAccessListStore>();
        throwingStore.Exists(Arg.Any<ulong>(), Arg.Any<Hash256>()).Returns(true);
        throwingStore.Get(Arg.Any<ulong>(), Arg.Any<Hash256>()).Returns(_ => throw new InvalidOperationException("boom"));
        FlatBalHealing healing = new(_blockTree, throwingStore, _reassembler, _persistence, _syncStore, _codeDb, LimboLogs.Instance);

        SeedInitialState(Acc(TestItem.AddressA, 100));
        BlockHeader firstPivot = Pivot(10, TestItem.KeccakA);
        BlockHeader lastPivot = SetupBlock(11, TestItem.KeccakB, BalanceBal(TestItem.AddressA, 150));

        bool result = await RunOnce(healing, firstPivot, lastPivot, [], default);

        Assert.That(result, Is.False);
        _syncStore.DidNotReceive().FinalizeSync(Arg.Any<BlockHeader>());
    }

    [Test]
    public async Task Falls_back_when_cancelled()
    {
        SeedInitialState(Acc(TestItem.AddressA, 100));
        BlockHeader firstPivot = Pivot(10, TestItem.KeccakA);
        BlockHeader lastPivot = SetupBlock(11, TestItem.KeccakB, BalanceBal(TestItem.AddressA, 150));

        bool result = await RunOnce(_healing, firstPivot, lastPivot, [], new CancellationToken(canceled: true));

        Assert.That(result, Is.False);
        _syncStore.DidNotReceive().FinalizeSync(Arg.Any<BlockHeader>());
    }

    // Differential test: for a random base state + random BAL, the trusted reference
    // (BlockAccessListManager.ApplyStateChanges over a real world state) produces the authoritative
    // post-block root. Flat BAL healing must reach the same root. Any seed that fails is a deterministic,
    // minimal-ish repro that names the exact BAL construct the flat apply mishandles.
    private static readonly IReleaseSpec FuzzSpec = Amsterdam.Instance; // post-EIP-158, matches the flat apply's clearing

    private static IEnumerable<int> FuzzSeeds() => Enumerable.Range(0, 500);

    [TestCaseSource(nameof(FuzzSeeds))]
    public async Task Matches_reference_apply_for_random_bal(int seed)
    {
        Random rng = new(seed);
        AccountSpec[] baseAccounts = RandomBaseAccounts(rng);
        ReadOnlyAccountChanges[] changes = RandomChanges(rng, baseAccounts);
        ReadOnlyBlockAccessList bal = Bal(changes);

        // Oracle: apply the BAL to a trie-backed world state seeded with the same accounts.
        (Hash256 referenceBase, Hash256 expected) = ReferenceApply(baseAccounts, bal);

        // Seed the flat state with the same accounts; its base root must match the trie base root.
        Hash256 flatBase = CommitState(_persistence, baseAccounts);
        Assert.That(flatBase, Is.EqualTo(referenceBase), $"seed {seed}: base seeding inconsistent (harness bug)");

        BlockHeader firstPivot = Pivot(10, referenceBase);
        BlockHeader lastPivot = SetupBlock(11, expected, bal);

        bool ok = await RunOnce(_healing, firstPivot, lastPivot, [], default);

        Assert.That(ok, Is.True, $"seed {seed}: flat healing did not reach reference root {expected}.\nBAL:\n  {string.Join("\n  ", changes.Select(c => c.ToString()))}");
    }

    // Same differential oracle, but forces the trie reassembler to rebuild every contract's storage trie
    // (updatedStorages = all base contracts) before applying the BAL. Isolates reassembly bugs: the rebuilt
    // roots must reproduce the base state exactly so the subsequent apply still reaches the reference root.
    [TestCaseSource(nameof(FuzzSeeds))]
    public async Task Matches_reference_apply_with_storage_reassembly(int seed)
    {
        Random rng = new(seed);
        AccountSpec[] baseAccounts = RandomBaseAccounts(rng);
        ReadOnlyAccountChanges[] changes = RandomChanges(rng, baseAccounts);
        ReadOnlyBlockAccessList bal = Bal(changes);

        (Hash256 referenceBase, Hash256 expected) = ReferenceApply(baseAccounts, bal);
        Hash256 flatBase = CommitState(_persistence, baseAccounts);
        Assert.That(flatBase, Is.EqualTo(referenceBase), $"seed {seed}: base seeding inconsistent (harness bug)");

        Hash256[] updatedStorages = baseAccounts.Where(a => a.Slots.Length > 0).Select(a => StorageOf(a.Address)).ToArray();

        BlockHeader firstPivot = Pivot(10, referenceBase);
        BlockHeader lastPivot = SetupBlock(11, expected, bal);

        bool ok = await RunOnce(_healing, firstPivot, lastPivot, updatedStorages, default);

        Assert.That(ok, Is.True, $"seed {seed}: healing with reassembly did not reach reference root {expected}.\nBAL:\n  {string.Join("\n  ", changes.Select(c => c.ToString()))}");
    }

    // Multi-block differential oracle. A sequence of BALs (all inside a single 256-block chunk) is applied
    // block-by-block to the reference world state, while flat healing sees every block in one chunk and must
    // reach the final root by aggregating their per-account deltas. This is the only test that exercises
    // ApplyChunk's cross-block aggregation — last-writer-wins balance/nonce/code, per-slot last value, and
    // mid-sequence account creation/emptying — against the trusted per-block reference.
    [TestCaseSource(nameof(FuzzSeeds))]
    public async Task Matches_reference_apply_for_random_bal_sequence(int seed)
    {
        Random rng = new(seed);
        AccountSpec[] baseAccounts = RandomBaseAccounts(rng);
        HashSet<Address> storageTouched = [];
        ReadOnlyBlockAccessList[] bals = RandomChangeSequence(rng, baseAccounts, storageTouched);

        (Hash256 referenceBase, Hash256[] roots) = ReferenceApplySequence(baseAccounts, bals);
        Hash256 expected = roots[^1];

        Hash256 flatBase = CommitState(_persistence, baseAccounts);
        Assert.That(flatBase, Is.EqualTo(referenceBase), $"seed {seed}: base seeding inconsistent (harness bug)");

        // Storage tries healing reassembles from the flat store: only accounts that already carry storage in
        // the base state. A fresh contract created mid-sequence has no flat leaves to reassemble from — its
        // storage tree is built from scratch during apply — so it must NOT be listed here (matching real
        // snap sync, where updatedStorages names only pre-existing storage that was re-synced).
        HashSet<Hash256> updatedStorages = [];
        foreach (AccountSpec a in baseAccounts)
            if (a.Slots.Length > 0) updatedStorages.Add(StorageOf(a.Address));

        BlockHeader firstPivot = Pivot(0, referenceBase);
        BlockHeader lastPivot = firstPivot;
        for (int i = 0; i < bals.Length; i++)
            lastPivot = SetupBlock((ulong)(i + 1), roots[i], bals[i]);

        bool ok = await RunOnce(_healing, firstPivot, lastPivot, updatedStorages, default);

        Assert.That(ok, Is.True, $"seed {seed}: {bals.Length}-block healing did not reach reference root {expected}");
    }

    [Explicit("diagnostic — sequence fuzzer")]
    [TestCase(499)]
    public async Task Diagnose_failing_sequence(int seed)
    {
        Random rng = new(seed);
        AccountSpec[] baseAccounts = RandomBaseAccounts(rng);
        HashSet<Address> storageTouched = [];
        ReadOnlyBlockAccessList[] bals = RandomChangeSequence(rng, baseAccounts, storageTouched);

        TestContext.Out.WriteLine("BASE:");
        foreach (AccountSpec a in baseAccounts)
            TestContext.Out.WriteLine($"  {a.Address} bal={a.Balance} nonce={a.Nonce} code={(a.Code is null ? "-" : a.Code.Length + "b")} slots={a.Slots.Length}");
        List<string> blockDump = [];
        for (int i = 0; i < bals.Length; i++)
        {
            blockDump.Add($"BLOCK {i + 1}:");
            foreach (ReadOnlyAccountChanges c in bals[i].AccountChanges)
            {
                string bch = c.BalanceChanges.Length > 0 ? string.Join(",", c.BalanceChanges.ToArray().Select(x => x.Value.ToString())) : "-";
                string nch = c.NonceChanges.Length > 0 ? string.Join(",", c.NonceChanges.ToArray().Select(x => x.Value.ToString())) : "-";
                string cch = c.CodeChanges.Length > 0 ? "code" : "-";
                string sch = c.StorageChanges.Length > 0 ? $"stor[{c.StorageChanges.Length}]" : "-";
                blockDump.Add($"    {c.Address} bal={bch} nonce={nch} code={cch} {sch} hasChanges={c.HasStateChanges}");
            }
        }

        (Hash256 referenceBase, Hash256[] roots) = ReferenceApplySequence(baseAccounts, bals);
        Hash256 expected = roots[^1];

        // Oracle self-consistency: apply the whole sequence within a single scope and compare the final root.
        IWorldState single = TestWorldStateFactory.CreateForTest();
        Hash256 singleBase = SeedReferenceBase(single, baseAccounts);
        Hash256 singleFinal;
        BlockHeader baseBlock = Build.A.BlockHeader.WithStateRoot(singleBase).WithNumber(0).TestObject;
        using (single.BeginScope(baseBlock))
        {
            foreach (ReadOnlyBlockAccessList bal in bals) ApplyReference(bal, single);
            singleFinal = single.StateRoot;
        }
        TestContext.Out.WriteLine($"baseRoot per-block={referenceBase} single={singleBase}");
        TestContext.Out.WriteLine($"final per-block-scope={expected} single-scope={singleFinal} equal={expected == singleFinal}");

        Hash256 flatBase = CommitState(_persistence, baseAccounts);
        TestContext.Out.WriteLine($"flatBase={flatBase} matches={flatBase == referenceBase}");

        HashSet<Hash256> updatedStorages = [];
        foreach (AccountSpec a in baseAccounts)
            if (a.Slots.Length > 0) updatedStorages.Add(StorageOf(a.Address));
        foreach (Address a in storageTouched) updatedStorages.Add(StorageOf(a));

        BlockHeader firstPivot = Pivot(0, referenceBase);
        BlockHeader lastPivot = firstPivot;
        for (int i = 0; i < bals.Length; i++)
            lastPivot = SetupBlock((ulong)(i + 1), roots[i], bals[i]);

        List<string> probe = [];
        for (int i = 0; i < bals.Length; i++)
        {
            BlockHeader h = _blockTree.FindHeader((ulong)(i + 1))!;
            bool exists = _balStore.Exists(h.Number, h.Hash!);
            ReadOnlyBlockAccessList? got = _balStore.Get(h.Number, h.Hash!);
            probe.Add($"blk {i + 1} num={h.Number} hash={h.Hash} root={h.StateRoot} exists={exists} got={(got is null ? "null" : "present")}");
        }

        bool ok = await RunOnce(_healing, firstPivot, lastPivot, updatedStorages, default);

        // Reference final per-account.
        Address[] allAddrs = baseAccounts.Select(a => a.Address)
            .Concat(bals.SelectMany(b => b.AccountChanges.Select(c => c.Address))).Distinct().ToArray();
        IWorldState refState = TestWorldStateFactory.CreateForTest();
        Hash256 rb = SeedReferenceBase(refState, baseAccounts);
        Dictionary<Address, string> reference = [];
        using (refState.BeginScope(Build.A.BlockHeader.WithStateRoot(rb).WithNumber(0).TestObject))
        {
            foreach (ReadOnlyBlockAccessList bal in bals) ApplyReference(bal, refState);
            foreach (Address addr in allAddrs)
                reference[addr] = refState.AccountExists(addr)
                    ? $"bal={refState.GetBalance(addr)} nonce={refState.GetNonce(addr)} codeHash={refState.GetCodeHash(addr)} storEmpty={refState.IsStorageEmpty(addr)}"
                    : "ABSENT";
        }

        using IPersistence.IPersistenceReader reader = _persistence.CreateReader(ReaderFlags.Sync);
        List<string> diff = [];
        foreach (Address addr in allAddrs)
        {
            Account? flat = reader.GetAccount(addr);
            string flatStr = flat is null ? "ABSENT" : $"bal={flat.Balance} nonce={flat.Nonce} codeHash={flat.CodeHash} storEmpty={flat.StorageRoot == Keccak.EmptyTreeHash}";
            bool mismatch = reference[addr] != flatStr;
            if (mismatch) diff.Add($">> {addr}\n     ref : {reference[addr]}\n     flat: {flatStr}");
        }
        Assert.Fail($"blocks={bals.Length} healed={ok}\nPROBE:\n{string.Join("\n", probe)}\nMISMATCHES:\n{string.Join("\n", diff)}");
    }

    // Manual tool: point at a seed reported failing by the fuzzers to get a per-account ref-vs-flat diff.
    [Explicit("diagnostic — run manually with a failing seed")]
    [TestCase(363)]
    public async Task Diagnose_failing_seed(int seed)
    {
        Random rng = new(seed);
        AccountSpec[] baseAccounts = RandomBaseAccounts(rng);
        ReadOnlyAccountChanges[] changes = RandomChanges(rng, baseAccounts);
        ReadOnlyBlockAccessList bal = Bal(changes);

        TestContext.Out.WriteLine("BASE:");
        foreach (AccountSpec a in baseAccounts)
            TestContext.Out.WriteLine($"  {a.Address} bal={a.Balance} nonce={a.Nonce} code={(a.Code is null ? "-" : a.Code.Length + "b")} slots={a.Slots.Length}");
        TestContext.Out.WriteLine("BAL:");
        foreach (ReadOnlyAccountChanges c in changes) TestContext.Out.WriteLine($"  {c}");

        // Reference post-state, account by account.
        IWorldState state = TestWorldStateFactory.CreateForTest();
        Hash256 baseRoot;
        using (state.BeginScope(IWorldState.PreGenesis))
        {
            foreach (AccountSpec a in baseAccounts)
            {
                state.CreateAccountIfNotExists(a.Address, a.Balance, a.Nonce);
                if (a.Code is { } code) state.InsertCode(a.Address, code, FuzzSpec);
                foreach (Slot s in a.Slots) state.Set(new StorageCell(a.Address, s.Key), s.Value);
            }
            state.Commit(FuzzSpec, isGenesis: true);
            state.CommitTree(0);
            baseRoot = state.StateRoot;
        }

        Address[] all = baseAccounts.Select(a => a.Address).Concat(changes.Select(c => c.Address)).Distinct().ToArray();
        BlockHeader baseBlock = Build.A.BlockHeader.WithStateRoot(baseRoot).WithNumber(0).TestObject;
        Dictionary<Address, string> reference = [];
        Hash256 postRoot;
        using (state.BeginScope(baseBlock))
        {
            ApplyReference(bal, state);
            foreach (Address addr in all)
            {
                reference[addr] = state.AccountExists(addr)
                    ? $"bal={state.GetBalance(addr)} nonce={state.GetNonce(addr)} codeHash={state.GetCodeHash(addr)} storageEmpty={state.IsStorageEmpty(addr)}"
                    : "ABSENT";
            }
            postRoot = state.StateRoot;
        }

        // Flat post-state after healing (writes are committed even when the final root check fails).
        CommitState(_persistence, baseAccounts);
        BlockHeader firstPivot = Pivot(10, baseRoot);
        BlockHeader lastPivot = SetupBlock(11, postRoot, bal);
        FlatBalHealing healing = new(_blockTree, _balStore, _reassembler, _persistence, _syncStore, _codeDb, new TestLogManager(LogLevel.Trace));
        bool healed = await RunOnce(healing, firstPivot, lastPivot, [], default);
        TestContext.Out.WriteLine($"healed={healed} postRoot={postRoot}");

        using IPersistence.IPersistenceReader reader = _persistence.CreateReader(ReaderFlags.Sync);
        List<string> diff = [];
        foreach (Address addr in all)
        {
            Account? flat = reader.GetAccount(addr);
            string flatStr = flat is null ? "ABSENT" : $"bal={flat.Balance} nonce={flat.Nonce} codeHash={flat.CodeHash} storageEmpty={flat.StorageRoot == Keccak.EmptyTreeHash}";
            string refStr = reference[addr];
            // Flat account is "slim": strip the codeHash-equal test to the same shape the reference reports.
            bool mismatch = refStr != flatStr;
            diff.Add($"{(mismatch ? ">>" : "  ")} {addr}\n     ref : {refStr}\n     flat: {flatStr}");
        }
        string report = "PER-ACCOUNT DIFF (>> = mismatch):\n" + string.Join("\n", diff);
        if (diff.Exists(l => l.StartsWith(">>"))) Assert.Fail(report);
        else Assert.Pass(report);
    }

    private static (Hash256 baseRoot, Hash256 postRoot) ReferenceApply(AccountSpec[] accounts, ReadOnlyBlockAccessList bal)
    {
        IWorldState state = TestWorldStateFactory.CreateForTest();
        Hash256 baseRoot = SeedReferenceBase(state, accounts);

        BlockHeader baseBlock = Build.A.BlockHeader.WithStateRoot(baseRoot).WithNumber(0).TestObject;
        Hash256 postRoot;
        using (state.BeginScope(baseBlock))
        {
            ApplyReference(bal, state);
            postRoot = state.StateRoot;
        }
        return (baseRoot, postRoot);
    }

    // Applies each block's BAL to the reference world state in sequence — every block committed on top of the
    // previous block's root — and returns the base root plus the post-state root after each block. This is the
    // per-block oracle for the multi-block fuzzer: flat healing sees the same blocks in one chunk and must
    // reproduce the final root by aggregating their deltas.
    private static (Hash256 baseRoot, Hash256[] roots) ReferenceApplySequence(AccountSpec[] accounts, ReadOnlyBlockAccessList[] bals)
    {
        IWorldState state = TestWorldStateFactory.CreateForTest();
        Hash256 baseRoot = SeedReferenceBase(state, accounts);

        Hash256[] roots = new Hash256[bals.Length];
        BlockHeader prev = Build.A.BlockHeader.WithStateRoot(baseRoot).WithNumber(0).TestObject;
        for (int i = 0; i < bals.Length; i++)
        {
            using (state.BeginScope(prev))
            {
                ApplyReference(bals[i], state);
                state.CommitTree(i + 1);
                roots[i] = state.StateRoot;
            }
            prev = Build.A.BlockHeader.WithStateRoot(roots[i]).WithNumber(i + 1).TestObject;
        }
        return (baseRoot, roots);
    }

    // Seeds a fresh pre-genesis scope with the base accounts and returns the committed base state root.
    private static Hash256 SeedReferenceBase(IWorldState state, AccountSpec[] accounts)
    {
        using (state.BeginScope(IWorldState.PreGenesis))
        {
            foreach (AccountSpec a in accounts)
            {
                state.CreateAccountIfNotExists(a.Address, a.Balance, a.Nonce);
                if (a.Code is { } code) state.InsertCode(a.Address, code, FuzzSpec);
                foreach (Slot s in a.Slots) state.Set(new StorageCell(a.Address, s.Key), s.Value);
            }
            state.Commit(FuzzSpec, isGenesis: true);
            state.CommitTree(0);
            return state.StateRoot;
        }
    }

    // Verbatim copy of BlockAccessListManager.ApplyStateChanges — the trusted oracle. Kept inline to avoid
    // a Nethermind.Consensus reference from this State test project. Must stay in sync with the original.
    private static void ApplyReference(ReadOnlyBlockAccessList bal, IWorldState stateProvider)
    {
        foreach (ReadOnlyAccountChanges accountChanges in bal.AccountChanges)
        {
            if (accountChanges.BalanceChanges.Length > 0)
            {
                stateProvider.CreateAccountIfNotExists(accountChanges.Address, 0, 0);
                UInt256 oldBalance = stateProvider.GetBalance(accountChanges.Address);
                UInt256 newBalance = accountChanges.BalanceChanges[^1].Value;
                if (newBalance > oldBalance)
                    stateProvider.AddToBalance(accountChanges.Address, newBalance - oldBalance, FuzzSpec);
                else if (newBalance < oldBalance)
                    stateProvider.SubtractFromBalance(accountChanges.Address, oldBalance - newBalance, FuzzSpec);
            }

            if (accountChanges.NonceChanges.Length > 0)
            {
                stateProvider.CreateAccountIfNotExists(accountChanges.Address, 0, 0);
                stateProvider.SetNonce(accountChanges.Address, accountChanges.NonceChanges[^1].Value);
            }

            if (accountChanges.CodeChanges.Length > 0)
                stateProvider.InsertCode(accountChanges.Address, accountChanges.CodeChanges[^1].Code, FuzzSpec);

            foreach (ReadOnlySlotChanges slotChange in accountChanges.StorageChanges)
            {
                if (slotChange.Changes.Length > 0)
                {
                    EvmWord value = slotChange.Changes[^1].Value;
                    ReadOnlySpan<byte> valueBytes = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<EvmWord, byte>(ref value), 32);
                    stateProvider.Set(new StorageCell(accountChanges.Address, slotChange.Key), [.. valueBytes.WithoutLeadingZeros()]);
                }
            }
        }
        stateProvider.Commit(FuzzSpec);
        stateProvider.RecalculateStateRoot();
    }

    private static Address FuzzAddr(int i)
    {
        byte[] bytes = new byte[20];
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), i);
        return new Address(bytes);
    }

    private static byte[] FuzzWord(Random rng) // minimal big-endian bytes of a random 0..2^64 value
    {
        byte[] full = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(full, (ulong)rng.NextInt64());
        ReadOnlySpan<byte> trimmed = full.AsSpan().WithoutLeadingZeros();
        return trimmed.IsZero() ? [] : trimmed.ToArray();
    }

    private static AccountSpec[] RandomBaseAccounts(Random rng)
    {
        int count = rng.Next(3, 9);
        List<AccountSpec> accounts = new(count);
        for (int i = 0; i < count; i++)
        {
            Address address = FuzzAddr(rng.Next(1, 24));
            if (accounts.Exists(a => a.Address == address)) continue;

            UInt256 balance = (UInt256)rng.Next(0, 500);
            ulong nonce = (ulong)rng.Next(0, 4);
            byte[]? code = rng.Next(100) < 25 ? RandomCode(rng) : null;
            // A contract always has a non-zero nonce, so never seed a "totally empty with code" account.
            if (code is not null && nonce == 0) nonce = 1;

            List<Slot> slots = [];
            if (code is not null && rng.Next(100) < 60)
                for (int s = rng.Next(1, 4); s > 0; s--)
                    slots.Add(new Slot((UInt256)rng.Next(1, 20), FuzzWord(rng) is { Length: > 0 } v ? v : [0x01]));

            accounts.Add(new AccountSpec(address, balance, nonce, code, [.. slots]));
        }
        return [.. accounts];
    }

    private static byte[] RandomCode(Random rng)
    {
        byte[] code = new byte[rng.Next(1, 12)];
        rng.NextBytes(code);
        return code;
    }

    private static ReadOnlyAccountChanges[] RandomChanges(Random rng, AccountSpec[] baseAccounts)
    {
        Dictionary<Address, AccountChangesBuilder> builders = [];

        AccountChangesBuilder For(Address address)
        {
            if (!builders.TryGetValue(address, out AccountChangesBuilder? b))
            {
                b = Build.An.AccountChanges.WithAddress(address);
                builders[address] = b;
            }
            return b;
        }

        // Candidate addresses: existing base accounts plus a few fresh ones (account creation).
        // Distinct so each address is built once with a single monotonic index sequence.
        List<Address> candidates = [.. baseAccounts.Select(a => a.Address)];
        for (int n = rng.Next(0, 3); n > 0; n--) candidates.Add(FuzzAddr(rng.Next(24, 40)));
        candidates = [.. candidates.Distinct()];
        HashSet<Address> baseAddrs = [.. baseAccounts.Select(a => a.Address)];
        HashSet<Address> baseContracts = [.. baseAccounts.Where(a => a.Code is not null).Select(a => a.Address)];

        foreach (Address address in candidates)
        {
            int roll = rng.Next(100);
            if (roll < 15) continue; // leave some untouched entirely

            // Change families are indexed by a strictly-increasing block-access index; per slot, a slot's
            // successive changes must also strictly increase. Use one monotonic counter for the account.
            uint index = 0;
            bool touched = false;
            bool exists = baseAddrs.Contains(address);
            bool isContract = baseContracts.Contains(address);
            if (rng.Next(100) < 60) { For(address).WithBalanceChanges(new BalanceChange(index++, (UInt256)rng.Next(0, 500))); touched = true; exists = true; }
            if (rng.Next(100) < 50) { For(address).WithNonceChanges(new NonceChange(index++, (ulong)rng.Next(0, 6))); touched = true; exists = true; }
            // A code change requires the account to exist first (contract creation always bumps the nonce);
            // InsertCode throws on a missing account, so never emit code for an uncreated fresh address.
            if (exists && rng.Next(100) < 20) { For(address).WithCodeChanges(new CodeChange(index++, RandomCode(rng))); touched = true; isContract = true; }
            // Storage only exists on contracts: a non-empty storage root implies code implies nonce >= 1, so
            // an account with storage is never EIP-158-empty. Emitting storage on a non-contract would
            // synthesize an unreachable state (empty account carrying a storage root).
            if (isContract && rng.Next(100) < 45)
            {
                for (int s = rng.Next(1, 4); s > 0; s--)
                {
                    UInt256 slot = (UInt256)rng.Next(1, 20);
                    UInt256 value = rng.Next(100) < 25 ? UInt256.Zero : (UInt256)rng.Next(1, 1000);
                    For(address).WithStorageChanges(slot, new StorageChange(index++, value));
                }
                touched = true;
            }

            // Some entries are pure reads (no changes) — must be preserved untouched by healing.
            if (!touched) For(address);
        }

        return builders.Values
            .Select(b => b.TestObject)
            .OrderBy(c => c.Address.ToString(), StringComparer.Ordinal)
            .ToArray();
    }

    // Builds a sequence of BALs (all within a single 256-block chunk) that stay internally consistent across
    // blocks: a mutable world model tracks which accounts currently exist and carry code, so no later block
    // references a deleted account (code) or attaches storage to a non-contract — both of which the reference
    // apply rejects. Addresses that receive storage writes anywhere in the sequence are collected in
    // <paramref name="storageTouched"/> so the caller knows which storage tries healing must rebuild.
    private static ReadOnlyBlockAccessList[] RandomChangeSequence(Random rng, AccountSpec[] baseAccounts, HashSet<Address> storageTouched)
    {
        Dictionary<Address, (UInt256 Balance, ulong Nonce, bool HasCode)> live = [];
        foreach (AccountSpec a in baseAccounts)
            live[a.Address] = (a.Balance, a.Nonce, a.Code is not null);

        int blocks = rng.Next(2, 12);
        int freshId = 24;
        ReadOnlyBlockAccessList[] bals = new ReadOnlyBlockAccessList[blocks];
        for (int b = 0; b < blocks; b++)
            bals[b] = RandomBlock(rng, live, storageTouched, ref freshId);
        return bals;
    }

    // One block of random changes over the current world model, mutating it to reflect the block's effect
    // (creation, code attachment, EIP-158 emptying) so subsequent blocks build on a consistent view.
    private static ReadOnlyBlockAccessList RandomBlock(
        Random rng,
        Dictionary<Address, (UInt256 Balance, ulong Nonce, bool HasCode)> live,
        HashSet<Address> storageTouched,
        ref int freshId)
    {
        Dictionary<Address, AccountChangesBuilder> builders = [];
        AccountChangesBuilder For(Address address)
        {
            if (!builders.TryGetValue(address, out AccountChangesBuilder? b))
                builders[address] = b = Build.An.AccountChanges.WithAddress(address);
            return b;
        }

        List<Address> candidates = [.. live.Keys];
        for (int n = rng.Next(0, 3); n > 0; n--) candidates.Add(FuzzAddr(freshId++));
        candidates = [.. candidates.Distinct()];

        foreach (Address address in candidates)
        {
            if (rng.Next(100) < 25) continue; // untouched this block

            uint index = 0;
            bool exists = live.TryGetValue(address, out (UInt256 Balance, ulong Nonce, bool HasCode) cur);
            UInt256 balance = exists ? cur.Balance : 0;
            ulong nonce = exists ? cur.Nonce : 0;
            bool hasCode = exists && cur.HasCode;
            bool touched = false;

            if (rng.Next(100) < 55)
            {
                balance = (UInt256)rng.Next(0, 500);
                For(address).WithBalanceChanges(new BalanceChange(index++, balance));
                exists = true; touched = true;
            }
            if (rng.Next(100) < 45)
            {
                nonce = (ulong)rng.Next(0, 6);
                For(address).WithNonceChanges(new NonceChange(index++, nonce));
                exists = true; touched = true;
            }
            // Code attaches only to an already-existing account (InsertCode throws otherwise) and, per EIP-6780,
            // is never removed once set — so only code-less existing accounts gain code.
            if (exists && !hasCode && rng.Next(100) < 15)
            {
                For(address).WithCodeChanges(new CodeChange(index++, RandomCode(rng)));
                hasCode = true; touched = true;
            }
            // Storage lives only on contracts (a non-empty storage root implies code implies nonce >= 1).
            if (hasCode && rng.Next(100) < 45)
            {
                for (int s = rng.Next(1, 4); s > 0; s--)
                {
                    UInt256 slot = (UInt256)rng.Next(1, 20);
                    UInt256 value = rng.Next(100) < 25 ? UInt256.Zero : (UInt256)rng.Next(1, 1000);
                    For(address).WithStorageChanges(slot, new StorageChange(index++, value));
                }
                storageTouched.Add(address);
                touched = true;
            }

            if (!touched) For(address); // pure read — must survive healing untouched

            if (exists)
            {
                // EIP-158: a touched account left with zero balance, zero nonce and no code is deleted.
                if (balance.IsZero && nonce == 0 && !hasCode) live.Remove(address);
                else live[address] = (balance, nonce, hasCode);
            }
        }

        return Bal(builders.Values
            .Select(b => b.TestObject)
            .OrderBy(c => c.Address.ToString(), StringComparer.Ordinal)
            .ToArray());
    }

    private readonly record struct Slot(UInt256 Key, byte[] Value);

    private sealed record AccountSpec(Address Address, UInt256 Balance, ulong Nonce, byte[]? Code, Slot[] Slots);

    private static AccountSpec Acc(Address address, UInt256 balance, ulong nonce = 0, byte[]? code = null, Slot[]? slots = null) =>
        new(address, balance, nonce, code, slots ?? []);

    private static Hash256 StorageOf(Address address) => address.ToAccountPath.ToCommitment();

    private bool FlatSlotExists(Address address, UInt256 slot)
    {
        using IPersistence.IPersistenceReader reader = _persistence.CreateReader(ReaderFlags.Sync);
        SlotValue value = default;
        return reader.TryGetSlot(address, slot, ref value);
    }

    // Composes the split IBalHealing primitives into a single-round heal (reassemble → apply → finalize),
    // mirroring one iteration of StateSyncRunner.RunBalHealing. Keeps these unit tests focused on the flat
    // apply logic; the multi-round orchestration is exercised at the runner level.
    private static Task<bool> RunOnce(FlatBalHealing healing, BlockHeader firstPivot, BlockHeader lastPivot, IReadOnlyCollection<Hash256> updatedStorages, CancellationToken token)
    {
        try
        {
            Hash256? root = healing.Reassemble(updatedStorages);
            if (root is null) return Task.FromResult(false);

            root = healing.ApplyRange(root, firstPivot, lastPivot, token);
            if (root is null) return Task.FromResult(false);

            healing.FinalizeSync(lastPivot);
            return Task.FromResult(true);
        }
        catch (OperationCanceledException)
        {
            return Task.FromResult(false);
        }
        catch (Exception)
        {
            return Task.FromResult(false);
        }
    }

    private static BlockHeader Pivot(ulong number, Hash256 stateRoot) =>
        Build.A.BlockHeader.WithNumber(number).WithStateRoot(stateRoot).TestObject;

    private BlockHeader SetupBlock(ulong number, Hash256 stateRoot, ReadOnlyBlockAccessList? bal)
    {
        BlockHeader header = Pivot(number, stateRoot);
        // FlatBalHealing calls the single-arg FindHeader(ulong); stub that exact overload.
        _blockTree.FindHeader(number).Returns(header);
        if (bal is not null)
            _balStore.Insert(number, header.Hash!, bal);
        return header;
    }

    private void SeedInitialState(params AccountSpec[] accounts) => CommitState(_persistence, accounts);

    private Hash256 BuildRoot(params AccountSpec[] accounts)
    {
        using SnapshotableMemColumnsDb<FlatDbColumns> db = new();
        RocksDbPersistence persistence = new(db, LimboLogs.Instance);
        return CommitState(persistence, accounts);
    }

    // Writes a consistent flat state (flat leaves + trie nodes) and returns the state root.
    private Hash256 CommitState(RocksDbPersistence persistence, AccountSpec[] accounts)
    {
        using IPersistence.IPersistenceReader reader = persistence.CreateReader(ReaderFlags.Sync);
        using IPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL);
        StateTree stateTree = new(new PersistenceTrieStoreAdapter(reader, batch, enableDoubleWriteCheck: false), LimboLogs.Instance);

        foreach (AccountSpec spec in accounts)
        {
            Account account = new(spec.Nonce, spec.Balance, Keccak.EmptyTreeHash, Keccak.OfAnEmptyString);

            if (spec.Code is { } code)
            {
                Hash256 codeHash = Keccak.Compute(code);
                _codeDb.Set(codeHash.Bytes, code);
                account = account.WithChangedCodeHash(codeHash);
            }

            if (spec.Slots is { Length: > 0 } slots)
            {
                StorageTree storage = new(
                    new PersistenceStorageTrieStoreAdapter(reader, batch, spec.Address.ToAccountPath.ToCommitment(), enableDoubleWriteCheck: false),
                    LimboLogs.Instance);
                foreach (Slot slot in slots)
                {
                    storage.Set(slot.Key, slot.Value);
                    batch.SetStorage(spec.Address, slot.Key, SlotValue.FromSpanWithoutLeadingZero(slot.Value));
                }
                storage.Commit();
                account = account.WithChangedStorageRoot(storage.RootHash);
            }

            stateTree.Set(spec.Address, account);
            batch.SetAccount(spec.Address, account);
        }

        stateTree.Commit();
        return stateTree.RootHash;
    }

    private static ReadOnlyBlockAccessList EmptyBal() => Build.A.BlockAccessList.TestObject;

    private static ReadOnlyBlockAccessList Bal(params ReadOnlyAccountChanges[] changes) =>
        Build.A.BlockAccessList.WithAccountChanges(changes).TestObject;

    private static ReadOnlyBlockAccessList BalanceBal(Address address, UInt256 balance) =>
        Bal(Build.An.AccountChanges.WithAddress(address).WithBalanceChanges(new BalanceChange(0, balance)).TestObject);

    private static ReadOnlyBlockAccessList StorageBal(Address address, UInt256 slot, UInt256 value) =>
        Bal(Build.An.AccountChanges.WithAddress(address).WithStorageChanges(slot, new StorageChange(0, value)).TestObject);
}
