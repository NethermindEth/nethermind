// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class SnapshotCompactorTests
{
    private SnapshotCompactor _compactor = null!;
    private ResourcePool _resourcePool = null!;
    private FlatDbConfig _config = null!;
    private SnapshotRepository _snapshotRepository;

    [SetUp]
    public void SetUp()
    {
        _config = new FlatDbConfig { CompactSize = 16 };
        _resourcePool = new ResourcePool(_config);
        _snapshotRepository = new SnapshotRepository(LimboLogs.Instance);
        _compactor = new SnapshotCompactor(_config, ScheduleHelper.CreateWithOffset(_config, 0), _resourcePool, _snapshotRepository, LimboLogs.Instance);
    }

    private static StateId CreateStateId(ulong blockNumber, byte rootByte = 0)
    {
        byte[] bytes = new byte[32];
        bytes[0] = rootByte;
        return new StateId(blockNumber, new ValueHash256(bytes));
    }

    private void BuildSnapshotChain(ulong startBlock, ulong endBlock)
    {
        for (ulong i = startBlock; i < endBlock; i++)
        {
            StateId from = CreateStateId(i);
            StateId to = CreateStateId(i + 1);
            Snapshot snapshot = _resourcePool.CreateSnapshot(from, to, ResourcePool.Usage.ReadOnlyProcessingEnv);

            bool added = _snapshotRepository.TryAddSnapshot(snapshot);
            Assert.That(added, Is.True, $"Failed to add snapshot {i}->{i + 1}");
            _snapshotRepository.AddStateId(to);
        }
    }

    private static void AssertSlotValueEqual(SlotValue? expected, SlotValue? actual)
    {
        Assert.That(actual, Is.Not.Null);
        Assert.That(actual!.Value.AsReadOnlySpan.ToArray(), Is.EqualTo(expected!.Value.AsReadOnlySpan.ToArray()));
    }

    private static void AssertAccountSame(Account expected, Account? actual)
    {
        Assert.That(actual, Is.Not.Null);
        Assert.That(actual!.Nonce, Is.EqualTo(expected.Nonce));
        Assert.That(actual!.Balance, Is.EqualTo(expected.Balance));
    }

    [Test]
    public void CompactSnapshotBundle_SingleSnapshot_ReturnsCorrectStateIds()
    {
        StateId from = new(0, Keccak.Zero);
        StateId to = new(1, Keccak.Zero);

        using Snapshot snapshot = _resourcePool.CreateSnapshot(from, to, ResourcePool.Usage.ReadOnlyProcessingEnv);
        Address address = new("0x1234567890123456789012345678901234567890");
        snapshot.Content.Accounts[address] = new Account(1, 100);

        SnapshotPooledList snapshots = new(1)
        {
            snapshot
        };

        using Snapshot compacted = _compactor.CompactSnapshotBundle(snapshots);

        Assert.That(compacted.From.BlockNumber, Is.EqualTo(0));
        Assert.That(compacted.To.BlockNumber, Is.EqualTo(1));
    }

    [Test]
    public void CompactSnapshotBundle_SingleSnapshot_PreservesAllDataTypes()
    {
        StateId from = new(0, Keccak.Zero);
        StateId to = new(1, Keccak.Zero);

        using Snapshot snapshot = _resourcePool.CreateSnapshot(from, to, ResourcePool.Usage.ReadOnlyProcessingEnv);
        Address address1 = new("0x1111111111111111111111111111111111111111");
        Address address2 = new("0x2222222222222222222222222222222222222222");
        UInt256 storageIndex1 = new(1);
        UInt256 storageIndex2 = new(2);
        TreePath statePath1 = TreePath.FromHexString("abcd");
        TreePath statePath2 = TreePath.FromHexString("ef01");
        TreePath storageNodePath1 = TreePath.FromHexString("1234");
        TreePath storageNodePath2 = TreePath.FromHexString("5678");
        Hash256 storageNodeHash1 = Keccak.Zero;
        Hash256 storageNodeHash2 = Keccak.Zero;
        SlotValue slotValue1 = new(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 100 });
        SlotValue slotValue2 = new(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 200 });

        // Add accounts
        snapshot.Content.Accounts[address1] = new Account(1, 100);
        snapshot.Content.Accounts[address2] = new Account(2, 200);

        // Add storage values
        snapshot.Content.Storages[(address1, storageIndex1)] = slotValue1;
        snapshot.Content.Storages[(address2, storageIndex2)] = slotValue2;

        // Add state nodes
        snapshot.Content.StateNodes[statePath1] = new TrieNode(NodeType.Leaf, storageNodeHash1);
        snapshot.Content.StateNodes[statePath2] = new TrieNode(NodeType.Branch, storageNodeHash2);

        // Add storage nodes
        Hash256 address1Hash = address1.ToAccountPath.ToCommitment();
        Hash256 address2Hash = address2.ToAccountPath.ToCommitment();
        snapshot.Content.StorageNodes[(address1Hash, storageNodePath1)] = new TrieNode(NodeType.Leaf, storageNodeHash1);
        snapshot.Content.StorageNodes[(address2Hash, storageNodePath2)] = new TrieNode(NodeType.Branch, storageNodeHash2);

        SnapshotPooledList snapshots = new(1)
        {
            snapshot
        };

        using Snapshot compacted = _compactor.CompactSnapshotBundle(snapshots);

        // Verify all data types are preserved
        Assert.That(compacted.AccountsCount, Is.EqualTo(2));
        AssertAccountSame(new Account(1, 100), compacted.Content.Accounts[address1]);
        AssertAccountSame(new Account(2, 200), compacted.Content.Accounts[address2]);

        Assert.That(compacted.StoragesCount, Is.EqualTo(2));
        AssertSlotValueEqual(slotValue1, compacted.Content.Storages[(address1, storageIndex1)]);
        AssertSlotValueEqual(slotValue2, compacted.Content.Storages[(address2, storageIndex2)]);

        Assert.That(compacted.StateNodesCount, Is.EqualTo(2));
        Assert.That(compacted.Content.StateNodes[statePath1].Keccak, Is.EqualTo(storageNodeHash1));
        Assert.That(compacted.Content.StateNodes[statePath2].Keccak, Is.EqualTo(storageNodeHash2));

        Assert.That(compacted.StorageNodesCount, Is.EqualTo(2));
    }

    [Test]
    public void CompactSnapshotBundle_MultipleSnapshots_MergesAllDataTypes()
    {
        Address address1 = new("0x1111111111111111111111111111111111111111");
        Address address2 = new("0x2222222222222222222222222222222222222222");
        UInt256 storageIndex1 = new(1);
        UInt256 storageIndex2 = new(2);
        TreePath statePath1 = TreePath.FromHexString("abcd");
        TreePath statePath2 = TreePath.FromHexString("ef01");
        TreePath storageNodePath1 = TreePath.FromHexString("1234");
        TreePath storageNodePath2 = TreePath.FromHexString("5678");
        SlotValue slotValue1 = new(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 100 });
        SlotValue slotValue2 = new(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 200 });

        // First snapshot
        StateId from0 = new(0, Keccak.Zero);
        StateId to0 = new(1, Keccak.Zero);
        using Snapshot snapshot0 = _resourcePool.CreateSnapshot(from0, to0, ResourcePool.Usage.ReadOnlyProcessingEnv);
        snapshot0.Content.Accounts[address1] = new Account(1, 100);
        snapshot0.Content.Storages[(address1, storageIndex1)] = slotValue1;
        snapshot0.Content.StateNodes[statePath1] = new TrieNode(NodeType.Leaf, Keccak.Zero);
        Hash256 address1Hash = address1.ToAccountPath.ToCommitment();
        snapshot0.Content.StorageNodes[(address1Hash, storageNodePath1)] = new TrieNode(NodeType.Leaf, Keccak.Zero);

        // Second snapshot with different items
        StateId from1 = new(1, Keccak.Zero);
        StateId to1 = new(2, Keccak.Zero);
        using Snapshot snapshot1 = _resourcePool.CreateSnapshot(from1, to1, ResourcePool.Usage.ReadOnlyProcessingEnv);
        snapshot1.Content.Accounts[address2] = new Account(2, 200);
        snapshot1.Content.Storages[(address2, storageIndex2)] = slotValue2;
        snapshot1.Content.StateNodes[statePath2] = new TrieNode(NodeType.Branch, Keccak.Zero);
        Hash256 address2Hash = address2.ToAccountPath.ToCommitment();
        snapshot1.Content.StorageNodes[(address2Hash, storageNodePath2)] = new TrieNode(NodeType.Branch, Keccak.Zero);

        SnapshotPooledList snapshots = new(2)
        {
            snapshot0,
            snapshot1
        };

        using Snapshot compacted = _compactor.CompactSnapshotBundle(snapshots);

        // Verify all items from both snapshots are merged
        Assert.That(compacted.AccountsCount, Is.EqualTo(2));
        Assert.That(compacted.StoragesCount, Is.EqualTo(2));
        Assert.That(compacted.StateNodesCount, Is.EqualTo(2));
        Assert.That(compacted.StorageNodesCount, Is.EqualTo(2));
    }

    [Test]
    public void CompactSnapshotBundle_MultipleSnapshots_LatestValueOverridesForAllDataTypes()
    {
        Address address = new("0x1111111111111111111111111111111111111111");
        UInt256 storageIndex = new(1);
        TreePath statePath = TreePath.FromHexString("abcd");
        TreePath storageNodePath = TreePath.FromHexString("1234");
        SlotValue slotValue1 = new(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 100 });
        SlotValue slotValue2 = new(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 200 });

        // First snapshot with initial values
        StateId from0 = new(0, Keccak.Zero);
        StateId to0 = new(1, Keccak.Zero);
        using Snapshot snapshot0 = _resourcePool.CreateSnapshot(from0, to0, ResourcePool.Usage.ReadOnlyProcessingEnv);
        snapshot0.Content.Accounts[address] = new Account(1, 100);
        snapshot0.Content.Storages[(address, storageIndex)] = slotValue1;
        snapshot0.Content.StateNodes[statePath] = new TrieNode(NodeType.Leaf, Keccak.Zero);
        Hash256 addressHash = address.ToAccountPath.ToCommitment();
        snapshot0.Content.StorageNodes[(addressHash, storageNodePath)] = new TrieNode(NodeType.Leaf, Keccak.Zero);

        // Second snapshot with updated values for same keys
        StateId from1 = new(1, Keccak.Zero);
        StateId to1 = new(2, Keccak.Zero);
        using Snapshot snapshot1 = _resourcePool.CreateSnapshot(from1, to1, ResourcePool.Usage.ReadOnlyProcessingEnv);
        snapshot1.Content.Accounts[address] = new Account(2, 200);
        snapshot1.Content.Storages[(address, storageIndex)] = slotValue2;
        snapshot1.Content.StateNodes[statePath] = new TrieNode(NodeType.Branch, Keccak.Zero);
        snapshot1.Content.StorageNodes[(addressHash, storageNodePath)] = new TrieNode(NodeType.Branch, Keccak.Zero);

        SnapshotPooledList snapshots = new(2)
        {
            snapshot0,
            snapshot1
        };

        using Snapshot compacted = _compactor.CompactSnapshotBundle(snapshots);

        // Verify latest values override earlier ones
        Assert.That(compacted.AccountsCount, Is.EqualTo(1));
        AssertAccountSame(new Account(2, 200), compacted.Content.Accounts[address]);

        Assert.That(compacted.StoragesCount, Is.EqualTo(1));
        AssertSlotValueEqual(slotValue2, compacted.Content.Storages[(address, storageIndex)]);

        Assert.That(compacted.StateNodesCount, Is.EqualTo(1));
        Assert.That(compacted.StorageNodesCount, Is.EqualTo(1));
    }

    [Test]
    public void CompactSnapshotBundle_SelfDestructedAddress_RemovesStorageAndNodes()
    {
        Address address = new("0x1111111111111111111111111111111111111111");
        UInt256 storageIndex = new(1);
        TreePath storagePath = TreePath.FromHexString("1234");
        Hash256 storageHash = Keccak.Zero;
        SlotValue slotValue = new(new byte[32]);

        StateId from0 = new(0, Keccak.Zero);
        StateId to0 = new(1, Keccak.Zero);
        using Snapshot snapshot0 = _resourcePool.CreateSnapshot(from0, to0, ResourcePool.Usage.ReadOnlyProcessingEnv);
        snapshot0.Content.Accounts[address] = new Account(1, 100);
        snapshot0.Content.Storages[(address, storageIndex)] = slotValue;
        snapshot0.Content.StorageNodes[(address.ToAccountPath.ToCommitment(), storagePath)] = new TrieNode(NodeType.Leaf, storageHash);

        StateId from1 = new(1, Keccak.Zero);
        StateId to1 = new(2, Keccak.Zero);
        using Snapshot snapshot1 = _resourcePool.CreateSnapshot(from1, to1, ResourcePool.Usage.ReadOnlyProcessingEnv);
        snapshot1.Content.SelfDestructedStorageAddresses[address] = false;

        SnapshotPooledList snapshots = new(2)
        {
            snapshot0,
            snapshot1
        };

        using Snapshot compacted = _compactor.CompactSnapshotBundle(snapshots);

        // Self-destructed address should be tracked, and its storage cleared
        Assert.That(compacted.Content.SelfDestructedStorageAddresses.Count, Is.GreaterThan(0));
        Assert.That(compacted.StoragesCount, Is.EqualTo(0));
        Assert.That(compacted.StorageNodesCount, Is.EqualTo(0));
    }

    [TestCase(false, TestName = "SelfDestructBoundary_KeepsWritesAtOrAfterTheClear")]
    [TestCase(true, TestName = "SelfDestructBoundary_LaterClearRaisesTheBoundary")]
    public void CompactSnapshotBundle_SelfDestructBoundary(bool selfDestructAgainInLastSnapshot)
    {
        Address a = new("0x1111111111111111111111111111111111111111");
        Address b = new("0x2222222222222222222222222222222222222222");
        Hash256 aHash = a.ToAccountPath.ToCommitment();
        Hash256 bHash = b.ToAccountPath.ToCommitment();
        TreePath pBefore = TreePath.FromHexString("01");
        TreePath pSame = TreePath.FromHexString("02");
        TreePath pAfter = TreePath.FromHexString("03");
        TreePath pB = TreePath.FromHexString("04");
        static SlotValue Slot(byte marker) => new(new byte[] { marker });
        static TrieNode Node() => new(NodeType.Leaf, Keccak.Zero);

        // Block 0 -> 1: A gets a slot/node written before it is ever self-destructed; B is unrelated.
        using Snapshot s0 = _resourcePool.CreateSnapshot(new(0, Keccak.Zero), new(1, Keccak.Zero), ResourcePool.Usage.ReadOnlyProcessingEnv);
        s0.Content.Storages[(a, new UInt256(1))] = Slot(0xA0);
        s0.Content.StorageNodes[(aHash, pBefore)] = Node();
        s0.Content.Storages[(b, new UInt256(1))] = Slot(0xBB);
        s0.Content.StorageNodes[(bHash, pB)] = Node();

        // Block 1 -> 2: A is self-destructed and re-written in the same snapshot (add after clear survives).
        using Snapshot s1 = _resourcePool.CreateSnapshot(new(1, Keccak.Zero), new(2, Keccak.Zero), ResourcePool.Usage.ReadOnlyProcessingEnv);
        s1.Content.SelfDestructedStorageAddresses[a] = false;
        s1.Content.Storages[(a, new UInt256(2))] = Slot(0xA2);
        s1.Content.StorageNodes[(aHash, pSame)] = Node();

        // Block 2 -> 3: A is written again, optionally self-destructed again (which raises the boundary).
        using Snapshot s2 = _resourcePool.CreateSnapshot(new(2, Keccak.Zero), new(3, Keccak.Zero), ResourcePool.Usage.ReadOnlyProcessingEnv);
        if (selfDestructAgainInLastSnapshot) s2.Content.SelfDestructedStorageAddresses[a] = false;
        s2.Content.Storages[(a, new UInt256(3))] = Slot(0xA3);
        s2.Content.StorageNodes[(aHash, pAfter)] = Node();

        SnapshotPooledList snapshots = new(3) { s0, s1, s2 };
        using Snapshot compacted = _compactor.CompactSnapshotBundle(snapshots);

        // Written strictly before the first clear: always removed.
        Assert.That(compacted.TryGetStorage((a, new UInt256(1)), out _), Is.False);
        Assert.That(compacted.TryGetStorageNode((aHash, pBefore), out _), Is.False);

        // Written after the last clear: always kept.
        Assert.That(compacted.TryGetStorage((a, new UInt256(3)), out SlotValue? slotAfter), Is.True);
        AssertSlotValueEqual(Slot(0xA3), slotAfter);
        Assert.That(compacted.TryGetStorageNode((aHash, pAfter), out _), Is.True);

        // Written in the first-clear block: kept unless a later clear raises the boundary past it.
        bool slotSameKept = !selfDestructAgainInLastSnapshot;
        Assert.That(compacted.TryGetStorage((a, new UInt256(2)), out _), Is.EqualTo(slotSameKept));
        Assert.That(compacted.TryGetStorageNode((aHash, pSame), out _), Is.EqualTo(slotSameKept));

        // Unrelated address is never touched by another address's self-destruct.
        Assert.That(compacted.TryGetStorage((b, new UInt256(1)), out SlotValue? slotB), Is.True);
        AssertSlotValueEqual(Slot(0xBB), slotB);
        Assert.That(compacted.TryGetStorageNode((bHash, pB), out _), Is.True);
    }

    [Test]
    public void CompactSnapshotBundle_NewAccountSelfDestruct_MarkedAsTrue()
    {
        Address address = new("0x1111111111111111111111111111111111111111");

        StateId from0 = new(0, Keccak.Zero);
        StateId to0 = new(1, Keccak.Zero);
        using Snapshot snapshot0 = _resourcePool.CreateSnapshot(from0, to0, ResourcePool.Usage.ReadOnlyProcessingEnv);

        StateId from1 = new(1, Keccak.Zero);
        StateId to1 = new(2, Keccak.Zero);
        using Snapshot snapshot1 = _resourcePool.CreateSnapshot(from1, to1, ResourcePool.Usage.ReadOnlyProcessingEnv);
        snapshot1.Content.SelfDestructedStorageAddresses[address] = true;

        SnapshotPooledList snapshots = new(2)
        {
            snapshot0,
            snapshot1
        };

        using Snapshot compacted = _compactor.CompactSnapshotBundle(snapshots);

        // New account marked as self-destructed should be tracked
        Assert.That(compacted.Content.SelfDestructedStorageAddresses.Count, Is.GreaterThan(0));
        // Verify at least one entry has true value
        Assert.That(compacted.Content.SelfDestructedStorageAddresses.Any(static kvp => kvp.Value), Is.True);
    }

    [Test]
    public void CompactSnapshotBundle_UsesCompactorUsageAtBoundary()
    {
        StateId from = new(0, Keccak.Zero);
        StateId to = new(16, Keccak.Zero);

        using Snapshot snapshot = _resourcePool.CreateSnapshot(from, to, ResourcePool.Usage.ReadOnlyProcessingEnv);

        SnapshotPooledList snapshots = new(1)
        {
            snapshot
        };

        using Snapshot compacted = _compactor.CompactSnapshotBundle(snapshots);

        Assert.That(compacted.Usage, Is.EqualTo(ResourcePool.Usage.Compact16));
    }

    [Test]
    public void CompactSnapshotBundle_UsesMidCompactorUsageNonBoundary()
    {
        StateId from = new(0, Keccak.Zero);
        StateId to = new(8, Keccak.Zero);

        using Snapshot snapshot = _resourcePool.CreateSnapshot(from, to, ResourcePool.Usage.ReadOnlyProcessingEnv);

        SnapshotPooledList snapshots = new(1)
        {
            snapshot
        };

        using Snapshot compacted = _compactor.CompactSnapshotBundle(snapshots);

        Assert.That(compacted.Usage, Is.EqualTo(ResourcePool.Usage.Compact8));
    }

    [Test]
    public void Debug_AssembleSnapshotsUntil_Works()
    {
        BuildSnapshotChain(0, 4);

        StateId target = CreateStateId(4);
        SnapshotPooledList assembled = _snapshotRepository.AssembleSnapshotsUntil(target, 0, 10);

        Assert.That(assembled.Count, Is.EqualTo(4));

        foreach (Snapshot s in assembled) s.Dispose();
        assembled.Dispose();
    }

    [Test]
    public void GetSnapshotsToCompact_CompactSizeDisabled_ReturnsEmpty()
    {
        FlatDbConfig config = new() { CompactSize = 1 };
        SnapshotCompactor compactor = new(config, ScheduleHelper.CreateWithOffset(config, 0), _resourcePool, _snapshotRepository, LimboLogs.Instance);

        StateId from = new(0, Keccak.Zero);
        StateId to = new(16, Keccak.Zero);
        using Snapshot snapshot = _resourcePool.CreateSnapshot(from, to, ResourcePool.Usage.ReadOnlyProcessingEnv);

        using SnapshotPooledList snapshots = compactor.GetSnapshotsToCompact(snapshot);

        Assert.That(snapshots.Count, Is.EqualTo(0));
    }

    [Test]
    public void GetSnapshotsToCompact_BlockZero_ReturnsEmpty()
    {
        StateId from = new(0, Keccak.Zero);
        StateId to = new(0, Keccak.Zero);
        using Snapshot snapshot = _resourcePool.CreateSnapshot(from, to, ResourcePool.Usage.ReadOnlyProcessingEnv);

        using SnapshotPooledList snapshots = _compactor.GetSnapshotsToCompact(snapshot);

        Assert.That(snapshots.Count, Is.EqualTo(0));
    }

    [Test]
    public void GetSnapshotsToCompact_NotCompactionBlock_ReturnsEmpty()
    {
        StateId from = new(0, Keccak.Zero);
        StateId to = new(5, Keccak.Zero);
        using Snapshot snapshot = _resourcePool.CreateSnapshot(from, to, ResourcePool.Usage.ReadOnlyProcessingEnv);

        using SnapshotPooledList snapshots = _compactor.GetSnapshotsToCompact(snapshot);

        Assert.That(snapshots.Count, Is.EqualTo(0));
    }

    [Test]
    public void GetSnapshotsToCompact_FullCompaction_ReturnsMultipleSnapshots()
    {
        // Build chain of 15 snapshots (0->1, 1->2, ..., 14->15)
        BuildSnapshotChain(0, 15);

        // Add the 16th snapshot (15->16) separately
        StateId targetFrom = CreateStateId(15);
        StateId targetTo = CreateStateId(16);
        Snapshot targetSnapshot = _resourcePool.CreateSnapshot(targetFrom, targetTo, ResourcePool.Usage.ReadOnlyProcessingEnv);
        _snapshotRepository.TryAddSnapshot(targetSnapshot);
        _snapshotRepository.AddStateId(targetTo);

        using SnapshotPooledList snapshots = _compactor.GetSnapshotsToCompact(targetSnapshot);

        Assert.That(snapshots.Count, Is.EqualTo(16));
    }

    [TestCase(4UL, 4)]   // 4 & -4 = 4, compact size 4, blocks 0->4
    [TestCase(8UL, 8)]   // 8 & -8 = 8, compact size 8, blocks 0->8
    [TestCase(12UL, 4)]  // 12 & -12 = 4, compact size 4, blocks 8->12
    public void GetSnapshotsToCompact_PowerOf2Compaction_ReturnsCorrectCount(ulong blockNumber, int expectedCount)
    {
        BuildSnapshotChain(0, blockNumber);

        StateId targetTo = CreateStateId(blockNumber);
        _snapshotRepository.TryLeaseState(targetTo, out Snapshot? targetSnapshot);

        using SnapshotPooledList snapshots = _compactor.GetSnapshotsToCompact(targetSnapshot!);

        Assert.That(snapshots.Count, Is.EqualTo(expectedCount));
        targetSnapshot!.Dispose();
    }

    [Test]
    public void GetSnapshotsToCompact_SingleSnapshot_ReturnsEmpty()
    {
        StateId from = new(0, Keccak.Zero);
        StateId to = new(16, Keccak.Zero);
        Snapshot snapshot = _resourcePool.CreateSnapshot(from, to, ResourcePool.Usage.ReadOnlyProcessingEnv);
        _snapshotRepository.TryAddSnapshot(snapshot);
        _snapshotRepository.AddStateId(to);

        using Snapshot targetSnapshot = _resourcePool.CreateSnapshot(from, to, ResourcePool.Usage.ReadOnlyProcessingEnv);

        using SnapshotPooledList snapshots = _compactor.GetSnapshotsToCompact(targetSnapshot);

        Assert.That(snapshots.Count, Is.EqualTo(0));
    }

    [Test]
    public void GetSnapshotsToCompact_IncompleteChain_ReturnsEmpty()
    {
        // Missing 1
        for (ulong i = 2; i < 16; i++)
        {
            StateId from = new(i, Keccak.Zero);
            StateId to = new(i + 1, Keccak.Zero);
            Snapshot snapshot = _resourcePool.CreateSnapshot(from, to, ResourcePool.Usage.ReadOnlyProcessingEnv);
            _snapshotRepository.TryAddSnapshot(snapshot);
            _snapshotRepository.AddStateId(to);
        }

        StateId targetFrom = new(15, Keccak.Zero);
        StateId targetTo = new(16, Keccak.Zero);
        using Snapshot targetSnapshot = _resourcePool.CreateSnapshot(targetFrom, targetTo, ResourcePool.Usage.ReadOnlyProcessingEnv);

        using SnapshotPooledList snapshots = _compactor.GetSnapshotsToCompact(targetSnapshot);

        Assert.That(snapshots.Count, Is.EqualTo(0));
    }

    [Test]
    public void DoCompactSnapshot_ValidChain_CreatesCompactedSnapshot()
    {
        // Build chain of 15 snapshots (0->1, 1->2, ..., 14->15)
        BuildSnapshotChain(0, 15);

        // Add the 16th snapshot (15->16) separately
        StateId targetFrom = CreateStateId(15);
        StateId targetTo = CreateStateId(16);
        Snapshot targetSnapshot = _resourcePool.CreateSnapshot(targetFrom, targetTo, ResourcePool.Usage.ReadOnlyProcessingEnv);
        targetSnapshot.Content.Accounts[TestItem.AddressB] = new Account(20UL, 2000UL);
        _snapshotRepository.TryAddSnapshot(targetSnapshot);
        _snapshotRepository.AddStateId(targetTo);

        _compactor.DoCompactSnapshot(targetSnapshot.To);

        Assert.That(_snapshotRepository.CompactedSnapshotCount, Is.EqualTo(1));
    }

    [Test]
    public void Constructor_NonPowerOf2CompactSize_Throws() =>
        Assert.Throws<ArgumentException>(() =>
            new CompactionSchedule(new MemDb(), new FlatDbConfig { CompactSize = 10 }, LimboLogs.Instance));

    [Test]
    public void GetSnapshotsToCompact_Size2Compaction_AllowedByDefault()
    {
        FlatDbConfig config = new() { CompactSize = 16 };
        SnapshotRepository repo = new(LimboLogs.Instance);
        SnapshotCompactor compactor = new(config, ScheduleHelper.CreateWithOffset(config, 0), _resourcePool, repo, LimboLogs.Instance);

        for (ulong i = 0; i < 2; i++)
        {
            StateId from = CreateStateId(i);
            StateId to = CreateStateId(i + 1);
            Snapshot snapshot = _resourcePool.CreateSnapshot(from, to, ResourcePool.Usage.ReadOnlyProcessingEnv);
            repo.TryAddSnapshot(snapshot);
            repo.AddStateId(to);
        }

        StateId target = CreateStateId(2);
        repo.TryLeaseState(target, out Snapshot? targetSnapshot);

        using SnapshotPooledList snapshots = compactor.GetSnapshotsToCompact(targetSnapshot!);

        Assert.That(snapshots.Count, Is.EqualTo(2));
        targetSnapshot!.Dispose();
    }

    [TestCase(1UL)]
    [TestCase(3UL)]
    [TestCase(5UL)]
    [TestCase(7UL)]
    [TestCase(9UL)]
    public void GetSnapshotsToCompact_OddBlock_ReturnsEmpty(ulong blockNumber)
    {
        BuildSnapshotChain(0, blockNumber);

        StateId from = CreateStateId(blockNumber - 1);
        StateId to = CreateStateId(blockNumber);
        using Snapshot snapshot = _resourcePool.CreateSnapshot(from, to, ResourcePool.Usage.ReadOnlyProcessingEnv);

        using SnapshotPooledList snapshots = _compactor.GetSnapshotsToCompact(snapshot);

        Assert.That(snapshots.Count, Is.EqualTo(0));
    }

    [TestCase(2UL, 2)]   // blockNumber & -blockNumber = 2
    [TestCase(4UL, 4)]
    [TestCase(6UL, 2)]
    [TestCase(8UL, 8)]
    [TestCase(10UL, 2)]
    [TestCase(12UL, 4)]
    [TestCase(14UL, 2)]
    [TestCase(16UL, 16)]
    public void GetSnapshotsToCompact_PowerOf2_CompactSizeMatchesBlockAlignment(ulong blockNumber, int expectedCompactSize)
    {
        int actualCompactSize = (int)Math.Min(blockNumber & (~blockNumber + 1UL), 16UL);
        Assert.That(actualCompactSize, Is.EqualTo(expectedCompactSize));
    }

    [Test]
    public void GetSnapshotsToCompact_WithOffset_FullCompactionShiftedFromBoundary()
    {
        // CompactSize=16, offset=3 -> full compaction triggers when (block+3) % 16 == 0,
        // i.e. at blocks 13, 29, 45, ... Build a chain to block 29 (second full boundary).
        FlatDbConfig config = new() { CompactSize = 16 };
        SnapshotRepository repo = new(LimboLogs.Instance);
        SnapshotCompactor compactor = new(config, ScheduleHelper.CreateWithOffset(config, 3), _resourcePool, repo, LimboLogs.Instance);

        for (ulong i = 0; i < 29; i++)
        {
            StateId from = CreateStateId(i);
            StateId to = CreateStateId(i + 1);
            Snapshot s = _resourcePool.CreateSnapshot(from, to, ResourcePool.Usage.ReadOnlyProcessingEnv);
            repo.TryAddSnapshot(s);
            repo.AddStateId(to);
        }

        // Block 29: (29+3) & -(29+3) = 32 & -32 = 32, capped at CompactSize=16 -> full compaction
        StateId target29 = CreateStateId(29);
        repo.TryLeaseState(target29, out Snapshot? targetSnapshot);
        using SnapshotPooledList snapshots29 = compactor.GetSnapshotsToCompact(targetSnapshot!);
        Assert.That(snapshots29.Count, Is.EqualTo(16), "Block 29 should trigger full compaction with offset=3");
        targetSnapshot!.Dispose();

        // Block 16: (16+3) & -(16+3) = 19 & -19 = 1 -> caller sees compactSize<=1, no compaction
        StateId target16 = CreateStateId(16);
        repo.TryLeaseState(target16, out targetSnapshot);
        using SnapshotPooledList snapshots16 = compactor.GetSnapshotsToCompact(targetSnapshot!);
        Assert.That(snapshots16.Count, Is.EqualTo(0), "Block 16 should NOT trigger compaction with offset=3");
        targetSnapshot!.Dispose();
    }

    [Test]
    public void CompactSnapshotBundle_WithOffset_UsesCorrectUsageTier()
    {
        // CompactSize=16, offset=3. At block 13 the bit trick yields 16 -> Compact16 tier.
        FlatDbConfig config = new() { CompactSize = 16 };
        SnapshotRepository repo = new(LimboLogs.Instance);
        SnapshotCompactor compactor = new(config, ScheduleHelper.CreateWithOffset(config, 3), _resourcePool, repo, LimboLogs.Instance);

        StateId from = new(0, Keccak.Zero);
        StateId to = new(13, Keccak.Zero);
        using Snapshot snapshot = _resourcePool.CreateSnapshot(from, to, ResourcePool.Usage.ReadOnlyProcessingEnv);

        SnapshotPooledList snapshots = new(1) { snapshot };

        using Snapshot compacted = compactor.CompactSnapshotBundle(snapshots);

        Assert.That(compacted.Usage, Is.EqualTo(ResourcePool.Usage.Compact16));
    }
}
