// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Collections;
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
        _compactor = new SnapshotCompactor(_config, _resourcePool, _snapshotRepository, LimboLogs.Instance);
    }

    private static StateId CreateStateId(long blockNumber, byte rootByte = 0)
    {
        byte[] bytes = new byte[32];
        bytes[0] = rootByte;
        return new StateId(blockNumber, new ValueHash256(bytes));
    }

    private void BuildSnapshotChain(long startBlock, long endBlock)
    {
        for (long i = startBlock; i < endBlock; i++)
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
        StateId from = new StateId(0, Keccak.Zero);
        StateId to = new StateId(1, Keccak.Zero);

        using Snapshot snapshot = _resourcePool.CreateSnapshot(from, to, ResourcePool.Usage.ReadOnlyProcessingEnv);
        Address address = new Address("0x1234567890123456789012345678901234567890");
        snapshot.Content.Accounts[address] = new Account(1, 100);

        SnapshotPooledList snapshots = new SnapshotPooledList(1);
        snapshots.Add(snapshot);

        using Snapshot compacted = _compactor.CompactSnapshotBundle(snapshots);

        Assert.That(compacted.From.BlockNumber, Is.EqualTo(0));
        Assert.That(compacted.To.BlockNumber, Is.EqualTo(1));
    }

    [Test]
    public void CompactSnapshotBundle_SingleSnapshot_PreservesAllDataTypes()
    {
        StateId from = new StateId(0, Keccak.Zero);
        StateId to = new StateId(1, Keccak.Zero);

        using Snapshot snapshot = _resourcePool.CreateSnapshot(from, to, ResourcePool.Usage.ReadOnlyProcessingEnv);
        Address address1 = new Address("0x1111111111111111111111111111111111111111");
        Address address2 = new Address("0x2222222222222222222222222222222222222222");
        UInt256 storageIndex1 = new UInt256(1);
        UInt256 storageIndex2 = new UInt256(2);
        TreePath statePath1 = TreePath.FromHexString("abcd");
        TreePath statePath2 = TreePath.FromHexString("ef01");
        TreePath storageNodePath1 = TreePath.FromHexString("1234");
        TreePath storageNodePath2 = TreePath.FromHexString("5678");
        Hash256 storageNodeHash1 = Keccak.Zero;
        Hash256 storageNodeHash2 = Keccak.Zero;
        SlotValue slotValue1 = new SlotValue(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 100 });
        SlotValue slotValue2 = new SlotValue(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 200 });

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

        SnapshotPooledList snapshots = new SnapshotPooledList(1);
        snapshots.Add(snapshot);

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
        Address address1 = new Address("0x1111111111111111111111111111111111111111");
        Address address2 = new Address("0x2222222222222222222222222222222222222222");
        UInt256 storageIndex1 = new UInt256(1);
        UInt256 storageIndex2 = new UInt256(2);
        TreePath statePath1 = TreePath.FromHexString("abcd");
        TreePath statePath2 = TreePath.FromHexString("ef01");
        TreePath storageNodePath1 = TreePath.FromHexString("1234");
        TreePath storageNodePath2 = TreePath.FromHexString("5678");
        SlotValue slotValue1 = new SlotValue(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 100 });
        SlotValue slotValue2 = new SlotValue(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 200 });

        // First snapshot
        StateId from0 = new StateId(0, Keccak.Zero);
        StateId to0 = new StateId(1, Keccak.Zero);
        using Snapshot snapshot0 = _resourcePool.CreateSnapshot(from0, to0, ResourcePool.Usage.ReadOnlyProcessingEnv);
        snapshot0.Content.Accounts[address1] = new Account(1, 100);
        snapshot0.Content.Storages[(address1, storageIndex1)] = slotValue1;
        snapshot0.Content.StateNodes[statePath1] = new TrieNode(NodeType.Leaf, Keccak.Zero);
        Hash256 address1Hash = address1.ToAccountPath.ToCommitment();
        snapshot0.Content.StorageNodes[(address1Hash, storageNodePath1)] = new TrieNode(NodeType.Leaf, Keccak.Zero);

        // Second snapshot with different items
        StateId from1 = new StateId(1, Keccak.Zero);
        StateId to1 = new StateId(2, Keccak.Zero);
        using Snapshot snapshot1 = _resourcePool.CreateSnapshot(from1, to1, ResourcePool.Usage.ReadOnlyProcessingEnv);
        snapshot1.Content.Accounts[address2] = new Account(2, 200);
        snapshot1.Content.Storages[(address2, storageIndex2)] = slotValue2;
        snapshot1.Content.StateNodes[statePath2] = new TrieNode(NodeType.Branch, Keccak.Zero);
        Hash256 address2Hash = address2.ToAccountPath.ToCommitment();
        snapshot1.Content.StorageNodes[(address2Hash, storageNodePath2)] = new TrieNode(NodeType.Branch, Keccak.Zero);

        SnapshotPooledList snapshots = new SnapshotPooledList(2);
        snapshots.Add(snapshot0);
        snapshots.Add(snapshot1);

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
        Address address = new Address("0x1111111111111111111111111111111111111111");
        UInt256 storageIndex = new UInt256(1);
        TreePath statePath = TreePath.FromHexString("abcd");
        TreePath storageNodePath = TreePath.FromHexString("1234");
        SlotValue slotValue1 = new SlotValue(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 100 });
        SlotValue slotValue2 = new SlotValue(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 200 });

        // First snapshot with initial values
        StateId from0 = new StateId(0, Keccak.Zero);
        StateId to0 = new StateId(1, Keccak.Zero);
        using Snapshot snapshot0 = _resourcePool.CreateSnapshot(from0, to0, ResourcePool.Usage.ReadOnlyProcessingEnv);
        snapshot0.Content.Accounts[address] = new Account(1, 100);
        snapshot0.Content.Storages[(address, storageIndex)] = slotValue1;
        snapshot0.Content.StateNodes[statePath] = new TrieNode(NodeType.Leaf, Keccak.Zero);
        Hash256 addressHash = address.ToAccountPath.ToCommitment();
        snapshot0.Content.StorageNodes[(addressHash, storageNodePath)] = new TrieNode(NodeType.Leaf, Keccak.Zero);

        // Second snapshot with updated values for same keys
        StateId from1 = new StateId(1, Keccak.Zero);
        StateId to1 = new StateId(2, Keccak.Zero);
        using Snapshot snapshot1 = _resourcePool.CreateSnapshot(from1, to1, ResourcePool.Usage.ReadOnlyProcessingEnv);
        snapshot1.Content.Accounts[address] = new Account(2, 200);
        snapshot1.Content.Storages[(address, storageIndex)] = slotValue2;
        snapshot1.Content.StateNodes[statePath] = new TrieNode(NodeType.Branch, Keccak.Zero);
        snapshot1.Content.StorageNodes[(addressHash, storageNodePath)] = new TrieNode(NodeType.Branch, Keccak.Zero);

        SnapshotPooledList snapshots = new SnapshotPooledList(2);
        snapshots.Add(snapshot0);
        snapshots.Add(snapshot1);

        using Snapshot compacted = _compactor.CompactSnapshotBundle(snapshots);

        // Verify latest values override earlier ones
        Assert.That(compacted.AccountsCount, Is.EqualTo(1));
        AssertAccountSame(new Account(2, 200), compacted.Content.Accounts[address]);

        Assert.That(compacted.StoragesCount, Is.EqualTo(1));
        AssertSlotValueEqual(slotValue2, compacted.Content.Storages[(address, storageIndex)]);

        Assert.That(compacted.StateNodesCount, Is.EqualTo(1));
        Assert.That(compacted.StateNodesCount, Is.EqualTo(1));
        Assert.That(compacted.StorageNodesCount, Is.EqualTo(1));
    }

    [Test]
    public void CompactSnapshotBundle_SelfDestructedAddress_RemovesStorageAndNodes()
    {
        Address address = new Address("0x1111111111111111111111111111111111111111");
        UInt256 storageIndex = new UInt256(1);
        TreePath storagePath = TreePath.FromHexString("1234");
        Hash256 storageHash = Keccak.Zero;
        SlotValue slotValue = new SlotValue(new byte[32]);

        StateId from0 = new StateId(0, Keccak.Zero);
        StateId to0 = new StateId(1, Keccak.Zero);
        using Snapshot snapshot0 = _resourcePool.CreateSnapshot(from0, to0, ResourcePool.Usage.ReadOnlyProcessingEnv);
        snapshot0.Content.Accounts[address] = new Account(1, 100);
        snapshot0.Content.Storages[(address, storageIndex)] = slotValue;
        snapshot0.Content.StorageNodes[(address.ToAccountPath.ToCommitment(), storagePath)] = new TrieNode(NodeType.Leaf, storageHash);

        StateId from1 = new StateId(1, Keccak.Zero);
        StateId to1 = new StateId(2, Keccak.Zero);
        using Snapshot snapshot1 = _resourcePool.CreateSnapshot(from1, to1, ResourcePool.Usage.ReadOnlyProcessingEnv);
        snapshot1.Content.SelfDestructedStorageAddresses[address] = false;

        SnapshotPooledList snapshots = new SnapshotPooledList(2);
        snapshots.Add(snapshot0);
        snapshots.Add(snapshot1);

        using Snapshot compacted = _compactor.CompactSnapshotBundle(snapshots);

        // Self-destructed address should be tracked, and its storage cleared
        Assert.That(compacted.Content.SelfDestructedStorageAddresses.Count, Is.GreaterThan(0));
        Assert.That(compacted.StoragesCount, Is.EqualTo(0));
        Assert.That(compacted.StorageNodesCount, Is.EqualTo(0));
    }

    [Test]
    public void CompactSnapshotBundle_NewAccountSelfDestruct_MarkedAsTrue()
    {
        Address address = new Address("0x1111111111111111111111111111111111111111");

        StateId from0 = new StateId(0, Keccak.Zero);
        StateId to0 = new StateId(1, Keccak.Zero);
        using Snapshot snapshot0 = _resourcePool.CreateSnapshot(from0, to0, ResourcePool.Usage.ReadOnlyProcessingEnv);

        StateId from1 = new StateId(1, Keccak.Zero);
        StateId to1 = new StateId(2, Keccak.Zero);
        using Snapshot snapshot1 = _resourcePool.CreateSnapshot(from1, to1, ResourcePool.Usage.ReadOnlyProcessingEnv);
        snapshot1.Content.SelfDestructedStorageAddresses[address] = true;

        SnapshotPooledList snapshots = new SnapshotPooledList(2);
        snapshots.Add(snapshot0);
        snapshots.Add(snapshot1);

        using Snapshot compacted = _compactor.CompactSnapshotBundle(snapshots);

        // New account marked as self-destructed should be tracked
        Assert.That(compacted.Content.SelfDestructedStorageAddresses.Count, Is.GreaterThan(0));
        // Verify at least one entry has true value
        Assert.That(compacted.Content.SelfDestructedStorageAddresses.Values.Any(v => v), Is.True);
    }

    [Test]
    public void CompactSnapshotBundle_UsesCompactorUsageAtBoundary()
    {
        StateId from = new StateId(0, Keccak.Zero);
        StateId to = new StateId(16, Keccak.Zero);

        using Snapshot snapshot = _resourcePool.CreateSnapshot(from, to, ResourcePool.Usage.ReadOnlyProcessingEnv);

        SnapshotPooledList snapshots = new SnapshotPooledList(1);
        snapshots.Add(snapshot);

        using Snapshot compacted = _compactor.CompactSnapshotBundle(snapshots);

        Assert.That(compacted.Usage, Is.EqualTo(ResourcePool.Usage.Compactor));
    }

    [Test]
    public void CompactSnapshotBundle_UsesMidCompactorUsageNonBoundary()
    {
        StateId from = new StateId(0, Keccak.Zero);
        StateId to = new StateId(15, Keccak.Zero);

        using Snapshot snapshot = _resourcePool.CreateSnapshot(from, to, ResourcePool.Usage.ReadOnlyProcessingEnv);

        SnapshotPooledList snapshots = new SnapshotPooledList(1);
        snapshots.Add(snapshot);

        using Snapshot compacted = _compactor.CompactSnapshotBundle(snapshots);

        Assert.That(compacted.Usage, Is.EqualTo(ResourcePool.Usage.MidCompactor));
    }

    #region GetSnapshotsToCompact Tests

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
        FlatDbConfig config = new FlatDbConfig { CompactSize = 0 };
        SnapshotCompactor compactor = new SnapshotCompactor(config, _resourcePool, _snapshotRepository, LimboLogs.Instance);

        StateId from = new StateId(0, Keccak.Zero);
        StateId to = new StateId(16, Keccak.Zero);
        using Snapshot snapshot = _resourcePool.CreateSnapshot(from, to, ResourcePool.Usage.ReadOnlyProcessingEnv);

        using SnapshotPooledList snapshots = compactor.GetSnapshotsToCompact(snapshot);

        Assert.That(snapshots.Count, Is.EqualTo(0));
    }

    [Test]
    public void GetSnapshotsToCompact_BlockZero_ReturnsEmpty()
    {
        StateId from = new StateId(0, Keccak.Zero);
        StateId to = new StateId(0, Keccak.Zero);
        using Snapshot snapshot = _resourcePool.CreateSnapshot(from, to, ResourcePool.Usage.ReadOnlyProcessingEnv);

        using SnapshotPooledList snapshots = _compactor.GetSnapshotsToCompact(snapshot);

        Assert.That(snapshots.Count, Is.EqualTo(0));
    }

    [Test]
    public void GetSnapshotsToCompact_NotCompactionBlock_ReturnsEmpty()
    {
        StateId from = new StateId(0, Keccak.Zero);
        StateId to = new StateId(5, Keccak.Zero);
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

    [Test]
    public void GetSnapshotsToCompact_MidCompaction_ReturnsMultipleSnapshots()
    {
        FlatDbConfig config = new FlatDbConfig { CompactSize = 16, MidCompactSize = 8 };
        SnapshotCompactor compactor = new SnapshotCompactor(config, _resourcePool, _snapshotRepository, LimboLogs.Instance);

        // Build chain of 7 snapshots (0->1, 1->2, ..., 6->7)
        BuildSnapshotChain(0, 7);

        // Add the 8th snapshot (7->8) separately
        StateId targetFrom = CreateStateId(7);
        StateId targetTo = CreateStateId(8);
        Snapshot targetSnapshot = _resourcePool.CreateSnapshot(targetFrom, targetTo, ResourcePool.Usage.ReadOnlyProcessingEnv);
        _snapshotRepository.TryAddSnapshot(targetSnapshot);
        _snapshotRepository.AddStateId(targetTo);

        using SnapshotPooledList snapshots = compactor.GetSnapshotsToCompact(targetSnapshot);

        Assert.That(snapshots.Count, Is.EqualTo(8));
    }

    [Test]
    public void GetSnapshotsToCompact_SingleSnapshot_ReturnsEmpty()
    {
        StateId from = new StateId(0, Keccak.Zero);
        StateId to = new StateId(16, Keccak.Zero);
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
        for (long i = 2; i < 16; i++)
        {
            StateId from = new StateId(i, Keccak.Zero);
            StateId to = new StateId(i + 1, Keccak.Zero);
            Snapshot snapshot = _resourcePool.CreateSnapshot(from, to, ResourcePool.Usage.ReadOnlyProcessingEnv);
            _snapshotRepository.TryAddSnapshot(snapshot);
            _snapshotRepository.AddStateId(to);
        }

        StateId targetFrom = new StateId(15, Keccak.Zero);
        StateId targetTo = new StateId(16, Keccak.Zero);
        using Snapshot targetSnapshot = _resourcePool.CreateSnapshot(targetFrom, targetTo, ResourcePool.Usage.ReadOnlyProcessingEnv);

        using SnapshotPooledList snapshots = _compactor.GetSnapshotsToCompact(targetSnapshot);

        Assert.That(snapshots.Count, Is.EqualTo(0));
    }

    #endregion

    #region DoCompactSnapshot Tests

    [Test]
    public void DoCompactSnapshot_ValidChain_CreatesCompactedSnapshot()
    {
        // Build chain of 15 snapshots (0->1, 1->2, ..., 14->15)
        BuildSnapshotChain(0, 15);

        // Add the 16th snapshot (15->16) separately
        StateId targetFrom = CreateStateId(15);
        StateId targetTo = CreateStateId(16);
        Snapshot targetSnapshot = _resourcePool.CreateSnapshot(targetFrom, targetTo, ResourcePool.Usage.ReadOnlyProcessingEnv);
        targetSnapshot.Content.Accounts[TestItem.AddressB] = new Account((UInt256)20, (UInt256)2000);
        _snapshotRepository.TryAddSnapshot(targetSnapshot);
        _snapshotRepository.AddStateId(targetTo);

        _compactor.DoCompactSnapshot(targetSnapshot.To);

        Assert.That(_snapshotRepository.CompactedSnapshotCount, Is.EqualTo(1));
    }

    #endregion

}
