// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ConcurrentCollections;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Headers;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;
using Nethermind.Evm.State;
using Nethermind.State;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Sync;
using Nethermind.State.Flat.Sync.Snap;
using Nethermind.State.Snap;
using Nethermind.Synchronization.FastSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Sync;

/// <summary>
/// Verifies that <see cref="FlatBalHealing"/> can rebuild the upper missing slice of a
/// state trie from the leaves + intact lower subtrees that snap sync leaves behind.
/// </summary>
[TestFixture]
public class TrieReassemblerTests
{
    private SnapshotableMemColumnsDb<FlatDbColumns> _columnsDb = null!;
    private IPersistence _persistence = null!;

    [SetUp]
    public void SetUp()
    {
        _columnsDb = new SnapshotableMemColumnsDb<FlatDbColumns>();
        _persistence = new RocksDbPersistence(_columnsDb);
    }

    [TearDown]
    public void TearDown() => _columnsDb.Dispose();

    [Test]
    public void Empty_persistence_returns_null()
    {
        FlatBalHealing reassembler = NewReassembler();
        Hash256? root = reassembler.ReassembleStateTrie();
        Assert.That(root, Is.Null);
    }

    /// <summary>
    /// Populate a real state trie via the snap-sync write path, drop every node above a chosen
    /// depth, then assert reassembly recovers the original root hash.
    /// </summary>
    [TestCase(2)]
    [TestCase(4)]
    [TestCase(8)]
    [TestCase(16)]
    public void Reassembles_state_trie_after_dropping_top_level_nodes(int accountCount)
    {
        // 1. Build a state trie + flat entries via snap sync's tree.
        Hash256 originalRoot = WriteAccountsViaSnapTree(accountCount);

        // 2. Drop only the path-0 (root) trie node — leave the depth-1+ children intact so
        //    reassembly has a valid spine to rebuild from.
        DeleteStateRoot();

        // 3. Reassemble. Expect the same root hash.
        FlatBalHealing reassembler = NewReassembler();
        Hash256? reassembled = reassembler.ReassembleStateTrie();

        Assert.That(reassembled, Is.EqualTo(originalRoot));
    }

    /// <summary>
    /// Forces the algorithm through the "collapse single child" path that produces an Extension.
    /// Two storage slots share a 4-nibble prefix, so the original trie has an <c>Extension(key=[1,2,3,4])</c>
    /// at the root pointing to a <c>Branch</c> at depth 4. Deleting only the root Extension makes
    /// reassembly recurse 4 levels (each with exactly one occupied child) and collapse them back
    /// into a single 4-nibble-key Extension — exercising the multi-level merge logic that prevents
    /// illegal <c>Extension→Extension</c> chains.
    /// </summary>
    [Test]
    public void Reassembles_root_extension_when_two_slots_share_prefix()
    {
        Hash256 accountHash = Keccak.Compute(TestItem.AddressA.Bytes);

        // Two slots whose paths share the first 4 nibbles (1,2,3,4) then diverge at nibble 4 (5 vs F).
        ValueHash256 slotA = HashFromNibbles(0x1, 0x2, 0x3, 0x4, 0x5);
        ValueHash256 slotB = HashFromNibbles(0x1, 0x2, 0x3, 0x4, 0xF);

        Hash256 originalRoot = WriteStorageSlots(accountHash, [
            new PathWithStorageSlot(slotA, Rlp.Encode((byte[])[0xAA]).Bytes),
            new PathWithStorageSlot(slotB, Rlp.Encode((byte[])[0xBB]).Bytes),
        ]);

        DeleteStorageRoot(accountHash);

        FlatBalHealing reassembler = NewReassembler();
        Hash256? reassembled = reassembler.ReassembleStorageTrie(accountHash.ValueHash256);

        Assert.That(reassembled, Is.EqualTo(originalRoot));
    }

    /// <summary>
    /// Multi-level missing: drop BOTH the root Extension at depth 0 AND the Branch at depth 4 it
    /// pointed to. Reassembly has to recurse 5 nibbles deep, build a new branch from the two
    /// surviving leaves, then collapse 4 single-child levels back up into the root Extension.
    /// </summary>
    [Test]
    public void Reassembles_multi_level_when_extension_and_branch_missing()
    {
        Hash256 accountHash = Keccak.Compute(TestItem.AddressA.Bytes);

        ValueHash256 slotA = HashFromNibbles(0x1, 0x2, 0x3, 0x4, 0x5);
        ValueHash256 slotB = HashFromNibbles(0x1, 0x2, 0x3, 0x4, 0xF);

        Hash256 originalRoot = WriteStorageSlots(accountHash, [
            new PathWithStorageSlot(slotA, Rlp.Encode((byte[])[0xAA]).Bytes),
            new PathWithStorageSlot(slotB, Rlp.Encode((byte[])[0xBB]).Bytes),
        ]);

        // Strip every storage trie node for this account with path length 0..4 — the Extension
        // (length 0) AND the Branch (length 4). The leaves at length 5 stay put.
        DeleteStorageTopNodes(accountHash, maxPathLength: 4);

        FlatBalHealing reassembler = NewReassembler();
        Hash256? reassembled = reassembler.ReassembleStorageTrie(accountHash.ValueHash256);

        Assert.That(reassembled, Is.EqualTo(originalRoot));
    }

    /// <summary>
    /// Same as above but only for the storage trie. We commit a storage trie under a single
    /// account, drop the top nodes, and confirm <see cref="FlatBalHealing.ReassembleStorageTrie"/>
    /// returns the original storage root.
    /// </summary>
    [TestCase(2)]
    [TestCase(8)]
    public void Reassembles_storage_trie_after_dropping_top_level_nodes(int slotCount)
    {
        Hash256 accountHash = Keccak.Compute(TestItem.AddressA.Bytes);

        Hash256 originalStorageRoot = WriteStorageViaSnapTree(accountHash, slotCount);

        // Drop only the path-0 storage root from StorageNodes.
        DeleteStorageRoot(accountHash);

        FlatBalHealing reassembler = NewReassembler();
        Hash256? reassembled = reassembler.ReassembleStorageTrie(accountHash.ValueHash256);

        Assert.That(reassembled, Is.EqualTo(originalStorageRoot));
    }

    private Hash256 WriteAccountsViaSnapTree(int accountCount)
    {
        // Generate `accountCount` distinct (addressHash, account) pairs, sorted by hash
        // because BulkSet requires WasSorted.
        List<PathWithAccount> entries = new(accountCount);
        for (int i = 0; i < accountCount; i++)
        {
            // Deterministic synthetic addresses; hash gives a well-distributed path.
            ValueHash256 addrHash = ValueKeccak.Compute(BitConverter.GetBytes((long)(i * 1_000_003 + 17)));
            Account account = new(nonce: (UInt256)i, balance: (UInt256)((i + 1) * 1_000));
            entries.Add(new PathWithAccount(addrHash, account));
        }

        entries.Sort((a, b) => a.Path.CompareTo(b.Path));

        IPersistence.IPersistenceReader reader = _persistence.CreateReader(ReaderFlags.Sync);
        IPersistence.IWriteBatch writeBatch = _persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL);
        using FlatSnapStateTree tree = new(reader, writeBatch, enableDoubleWriteCheck: false, LimboLogs.Instance);

        tree.BulkSetAndUpdateRootHash(entries);
        tree.Commit(ValueKeccak.MaxValue);

        return tree.RootHash;
    }

    private Hash256 WriteStorageViaSnapTree(Hash256 accountHash, int slotCount)
    {
        List<PathWithStorageSlot> slots = new(slotCount);
        for (int i = 0; i < slotCount; i++)
        {
            ValueHash256 slotHash = ValueKeccak.Compute(BitConverter.GetBytes((long)(i * 7_919 + 5)));
            byte[] value = BitConverter.GetBytes((long)(i + 1));
            slots.Add(new PathWithStorageSlot(slotHash, value));
        }
        slots.Sort((a, b) => a.Path.CompareTo(b.Path));

        IPersistence.IPersistenceReader reader = _persistence.CreateReader(ReaderFlags.Sync);
        IPersistence.IWriteBatch writeBatch = _persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL);
        using FlatSnapStorageTree tree = new(reader, writeBatch, accountHash, enableDoubleWriteCheck: false, LimboLogs.Instance);

        tree.BulkSetAndUpdateRootHash(slots);
        tree.Commit(ValueKeccak.MaxValue);

        return tree.RootHash;
    }

    /// <summary>
    /// Delete only the path-0 (root) entry from <see cref="FlatDbColumns.StateTopNodes"/>.
    /// The root key is <c>[0x00, 0x00, 0x00]</c> per <c>EncodeStateTopNodeKey</c>: 3 leading
    /// path bytes (all zero) with the length nibble (0) packed into byte 2's low nibble.
    /// Leaving the depth-1+ nodes intact gives reassembly a valid trie spine to rebuild from.
    /// </summary>
    private void DeleteStateRoot()
    {
        IDb top = _columnsDb.GetColumnDb(FlatDbColumns.StateTopNodes);
        top.Remove([0x00, 0x00, 0x00]);
    }

    /// <summary>
    /// Delete only the path-0 (storage root) entry for this account in <see cref="FlatDbColumns.StorageNodes"/>.
    /// Storage key layout: <c>[addr_prefix(4)][path-via-EncodeWith8Byte(8)][addr_suffix(16)]</c>.
    /// At path empty (length 0) the 8 path bytes are all zero, so we just zero them in the buffer.
    /// </summary>
    private void DeleteStorageRoot(Hash256 accountHash)
    {
        IDb storageNodes = _columnsDb.GetColumnDb(FlatDbColumns.StorageNodes);
        byte[] key = new byte[28];
        accountHash.Bytes[..4].CopyTo(key.AsSpan()[..4]);
        // path bytes 4..12 stay zero (path empty + length nibble 0)
        accountHash.Bytes[4..20].CopyTo(key.AsSpan()[12..28]);
        storageNodes.Remove(key);
    }

    /// <summary>
    /// Delete every storage trie node for this account with path length
    /// <c>0..</c><paramref name="maxPathLength"/> (inclusive). The path-length byte is packed
    /// into the low nibble of byte 11 (last byte of the 8-byte path segment that follows the
    /// 4-byte address prefix at bytes 0..3).
    /// </summary>
    private void DeleteStorageTopNodes(Hash256 accountHash, int maxPathLength)
    {
        IDb storageNodes = _columnsDb.GetColumnDb(FlatDbColumns.StorageNodes);
        byte[] addrPrefix = accountHash.Bytes[..4].ToArray();
        byte[][] keys = storageNodes.GetAllKeys()
            .Where(k => k.Length == 28
                     && k.AsSpan(0, 4).SequenceEqual(addrPrefix)
                     && (k[11] & 0x0F) <= maxPathLength)
            .ToArray();
        foreach (byte[] key in keys)
        {
            storageNodes.Remove(key);
        }
    }

    /// <summary>
    /// Build a 32-byte storage path whose first <paramref name="nibbles"/> are the given values
    /// and remaining nibbles are zero. Used to force a specific trie shape.
    /// </summary>
    private static ValueHash256 HashFromNibbles(params byte[] nibbles)
    {
        Span<byte> buf = stackalloc byte[32];
        for (int i = 0; i < nibbles.Length; i++)
        {
            int byteIdx = i / 2;
            buf[byteIdx] = (i % 2 == 0)
                ? (byte)((nibbles[i] & 0x0F) << 4)
                : (byte)(buf[byteIdx] | (nibbles[i] & 0x0F));
        }
        return new ValueHash256(buf);
    }

    private Hash256 WriteStorageSlots(Hash256 accountHash, PathWithStorageSlot[] slots)
    {
        Array.Sort(slots, (a, b) => a.Path.CompareTo(b.Path));

        IPersistence.IPersistenceReader reader = _persistence.CreateReader(ReaderFlags.Sync);
        IPersistence.IWriteBatch writeBatch = _persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL);
        using FlatSnapStorageTree tree = new(reader, writeBatch, accountHash, enableDoubleWriteCheck: false, LimboLogs.Instance);

        tree.BulkSetAndUpdateRootHash(slots);
        tree.Commit(ValueKeccak.MaxValue);

        return tree.RootHash;
    }

    /// <summary>
    /// Construct a FlatBalHealing with substitutes for the dependencies the reassembly methods
    /// don't touch. Used by the tests that only exercise <see cref="FlatBalHealing.ReassembleStateTrie"/>
    /// / <see cref="FlatBalHealing.ReassembleStorageTrie"/>. Tests that exercise
    /// <see cref="FlatBalHealing.Run"/> build a configured instance inline.
    /// </summary>
    private FlatBalHealing NewReassembler() =>
        new(_persistence,
            Substitute.For<ITreeSyncStore>(),
            Substitute.For<IWorldStateManager>(),
            Substitute.For<IBlockTree>(),
            Substitute.For<IBlockAccessListStore>(),
            Substitute.For<ISpecProvider>(),
            LimboLogs.Instance);

    /// <summary>
    /// Builds a pivot pair where first and last are the same header — the no-BAL-bridge case.
    /// </summary>
    private static (BlockHeader firstPivot, BlockHeader lastPivot) SamePivot(Hash256 stateRoot, long blockNumber = 100) =>
        (PivotHeader(stateRoot, blockNumber), PivotHeader(stateRoot, blockNumber));

    private static BlockHeader PivotHeader(Hash256 stateRoot, long number, Hash256? parentHash = null)
    {
        BlockHeaderBuilder builder = Build.A.BlockHeader.WithStateRoot(stateRoot).WithNumber(number);
        if (parentHash is not null) builder = builder.WithParentHash(parentHash);
        BlockHeader header = builder.TestObject;
        // Deterministic synthetic hash so tests are stable across runs without computing the real keccak.
        header.Hash = ValueKeccak.Compute($"pivot-{number}-{stateRoot}").ToCommitment();
        return header;
    }

    private static IBlockTree StubBlockTree(params (BlockHeader child, BlockHeader? parent)[] parents)
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        foreach ((BlockHeader child, BlockHeader? parent) in parents)
        {
            blockTree.FindHeader(child.ParentHash!, Arg.Any<BlockTreeLookupOptions>(), Arg.Any<long?>()).Returns(parent);
        }
        return blockTree;
    }

    private static ISpecProvider StubSpec()
    {
        ISpecProvider provider = Substitute.For<ISpecProvider>();
        // GetSpec(BlockHeader) is an extension that delegates to GetSpec(ForkActivation); stub the underlying method.
        provider.GetSpec(Arg.Any<ForkActivation>()).Returns(Cancun.Instance);
        return provider;
    }

    private static IReadOnlyCollection<Hash256> NoUpdatedStorages => Array.Empty<Hash256>();

    /// <summary>
    /// End-to-end smoke test for <see cref="FlatBalHealing.Run"/>: build a real 4-account trie via
    /// the snap-sync write path, drop the root, and assert that <c>Run</c> finalizes when the
    /// first and last pivot point at the same header (no BAL bridge needed).
    /// </summary>
    [Test]
    public async Task Run_finalizes_when_first_equals_last_pivot()
    {
        Hash256 originalRoot = WriteAccountsViaSnapTree(accountCount: 4);
        DeleteStateRoot();

        (BlockHeader first, BlockHeader last) = SamePivot(originalRoot);

        ITreeSyncStore treeSyncStore = Substitute.For<ITreeSyncStore>();
        IWorldStateManager worldStateManager = Substitute.For<IWorldStateManager>();
        worldStateManager.VerifyTrie(last, Arg.Any<CancellationToken>()).Returns(true);

        FlatBalHealing healing = new(
            _persistence, treeSyncStore, worldStateManager,
            Substitute.For<IBlockTree>(),
            Substitute.For<IBlockAccessListStore>(),
            StubSpec(),
            LimboLogs.Instance);

        bool result = await healing.Run(first, last, NoUpdatedStorages, CancellationToken.None);

        Assert.That(result, Is.True);
        treeSyncStore.Received(1).FinalizeSync(last);
        worldStateManager.Received(1).VerifyTrie(last, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// When reassembly produces a root that does not match the first pivot's expected root,
    /// <c>Run</c> must return <see langword="false"/> and NOT finalize the sync — the caller
    /// falls through to traditional healing.
    /// </summary>
    [Test]
    public async Task Run_returns_false_without_finalizing_when_root_mismatches()
    {
        WriteAccountsViaSnapTree(accountCount: 4);
        DeleteStateRoot();

        // First pivot expects a root that does NOT match what reassembly will produce.
        (BlockHeader first, BlockHeader last) = SamePivot(Keccak.Zero);

        ITreeSyncStore treeSyncStore = Substitute.For<ITreeSyncStore>();
        IWorldStateManager worldStateManager = Substitute.For<IWorldStateManager>();

        FlatBalHealing healing = new(
            _persistence, treeSyncStore, worldStateManager,
            Substitute.For<IBlockTree>(),
            Substitute.For<IBlockAccessListStore>(),
            StubSpec(),
            LimboLogs.Instance);

        bool result = await healing.Run(first, last, NoUpdatedStorages, CancellationToken.None);

        Assert.That(result, Is.False);
        treeSyncStore.DidNotReceive().FinalizeSync(Arg.Any<BlockHeader>());
        worldStateManager.DidNotReceive().VerifyTrie(Arg.Any<BlockHeader>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Bridge with one block of BAL replay: first pivot has a state matching reassembly; last
    /// pivot is one block ahead with the SAME state root (we use an empty BAL so the state
    /// doesn't change). Confirms the chain walk, BAL lookup, and verify-trie all fire.
    /// </summary>
    [Test]
    public async Task Run_replays_one_block_bal_and_finalizes_at_last_pivot()
    {
        Hash256 originalRoot = WriteAccountsViaSnapTree(accountCount: 4);
        DeleteStateRoot();

        BlockHeader first = PivotHeader(originalRoot, number: 100);
        BlockHeader last = PivotHeader(originalRoot, number: 101, parentHash: first.Hash);

        IBlockTree blockTree = StubBlockTree((last, first));
        IBlockAccessListStore balStore = Substitute.For<IBlockAccessListStore>();
        balStore.Get(last.Hash!).Returns(new BlockAccessList());

        // Wire a stub scope that reports the unchanged root after replay.
        IWorldStateManager worldStateManager = Substitute.For<IWorldStateManager>();
        IWorldStateScopeProvider scopeProvider = Substitute.For<IWorldStateScopeProvider>();
        IWorldStateScopeProvider.IScope scope = Substitute.For<IWorldStateScopeProvider.IScope>();
        scope.RootHash.Returns(originalRoot);
        scopeProvider.BeginScope(Arg.Any<BlockHeader?>()).Returns(scope);
        worldStateManager.GlobalWorldState.Returns(scopeProvider);
        worldStateManager.VerifyTrie(last, Arg.Any<CancellationToken>()).Returns(true);

        ITreeSyncStore treeSyncStore = Substitute.For<ITreeSyncStore>();
        FlatBalHealing healing = new(_persistence, treeSyncStore, worldStateManager,
            blockTree, balStore, StubSpec(), LimboLogs.Instance);

        bool result = await healing.Run(first, last, NoUpdatedStorages, CancellationToken.None);

        Assert.That(result, Is.True);
        balStore.Received(1).Get(last.Hash!);
        treeSyncStore.Received(1).FinalizeSync(last);
        worldStateManager.Received(1).VerifyTrie(last, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Run_returns_false_when_bal_is_missing_in_db()
    {
        Hash256 originalRoot = WriteAccountsViaSnapTree(accountCount: 4);
        DeleteStateRoot();

        BlockHeader first = PivotHeader(originalRoot, number: 100);
        BlockHeader last = PivotHeader(originalRoot, number: 101, parentHash: first.Hash);

        IBlockTree blockTree = StubBlockTree((last, first));
        IBlockAccessListStore balStore = Substitute.For<IBlockAccessListStore>();
        balStore.Get(last.Hash!).Returns((BlockAccessList?)null);

        IWorldStateManager worldStateManager = Substitute.For<IWorldStateManager>();
        IWorldStateScopeProvider scopeProvider = Substitute.For<IWorldStateScopeProvider>();
        IWorldStateScopeProvider.IScope scope = Substitute.For<IWorldStateScopeProvider.IScope>();
        scopeProvider.BeginScope(Arg.Any<BlockHeader?>()).Returns(scope);
        worldStateManager.GlobalWorldState.Returns(scopeProvider);

        ITreeSyncStore treeSyncStore = Substitute.For<ITreeSyncStore>();
        FlatBalHealing healing = new(_persistence, treeSyncStore, worldStateManager,
            blockTree, balStore, StubSpec(), LimboLogs.Instance);

        bool result = await healing.Run(first, last, NoUpdatedStorages, CancellationToken.None);

        Assert.That(result, Is.False);
        treeSyncStore.DidNotReceive().FinalizeSync(Arg.Any<BlockHeader>());
        worldStateManager.DidNotReceive().VerifyTrie(Arg.Any<BlockHeader>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Run_returns_false_when_parent_chain_does_not_connect()
    {
        Hash256 originalRoot = WriteAccountsViaSnapTree(accountCount: 4);
        DeleteStateRoot();

        BlockHeader first = PivotHeader(originalRoot, number: 100);
        BlockHeader last = PivotHeader(originalRoot, number: 102, parentHash: ValueKeccak.Compute("orphan-parent").ToCommitment());

        // BlockTree returns null for any parent lookup — chain doesn't connect to first pivot.
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.FindHeader(Arg.Any<Hash256>(), Arg.Any<BlockTreeLookupOptions>(), Arg.Any<long?>()).Returns((BlockHeader?)null);

        IBlockAccessListStore balStore = Substitute.For<IBlockAccessListStore>();
        ITreeSyncStore treeSyncStore = Substitute.For<ITreeSyncStore>();
        IWorldStateManager worldStateManager = Substitute.For<IWorldStateManager>();

        FlatBalHealing healing = new(_persistence, treeSyncStore, worldStateManager,
            blockTree, balStore, StubSpec(), LimboLogs.Instance);

        bool result = await healing.Run(first, last, NoUpdatedStorages, CancellationToken.None);

        Assert.That(result, Is.False);
        balStore.DidNotReceive().Get(Arg.Any<Hash256>());
        treeSyncStore.DidNotReceive().FinalizeSync(Arg.Any<BlockHeader>());
    }

    [Test]
    public async Task Run_returns_false_when_final_state_root_mismatches_last_pivot()
    {
        Hash256 originalRoot = WriteAccountsViaSnapTree(accountCount: 4);
        DeleteStateRoot();

        BlockHeader first = PivotHeader(originalRoot, number: 100);
        // last pivot's StateRoot intentionally differs from what an empty BAL would produce.
        BlockHeader last = PivotHeader(ValueKeccak.Compute("expected-different-root").ToCommitment(), number: 101, parentHash: first.Hash);

        IBlockTree blockTree = StubBlockTree((last, first));
        IBlockAccessListStore balStore = Substitute.For<IBlockAccessListStore>();
        balStore.Get(last.Hash!).Returns(new BlockAccessList());

        IWorldStateManager worldStateManager = Substitute.For<IWorldStateManager>();
        IWorldStateScopeProvider scopeProvider = Substitute.For<IWorldStateScopeProvider>();
        IWorldStateScopeProvider.IScope scope = Substitute.For<IWorldStateScopeProvider.IScope>();
        scope.RootHash.Returns(originalRoot);  // Empty BAL leaves root == originalRoot != last.StateRoot
        scopeProvider.BeginScope(Arg.Any<BlockHeader?>()).Returns(scope);
        worldStateManager.GlobalWorldState.Returns(scopeProvider);

        ITreeSyncStore treeSyncStore = Substitute.For<ITreeSyncStore>();
        FlatBalHealing healing = new(_persistence, treeSyncStore, worldStateManager,
            blockTree, balStore, StubSpec(), LimboLogs.Instance);

        bool result = await healing.Run(first, last, NoUpdatedStorages, CancellationToken.None);

        Assert.That(result, Is.False);
        treeSyncStore.DidNotReceive().FinalizeSync(Arg.Any<BlockHeader>());
        worldStateManager.DidNotReceive().VerifyTrie(Arg.Any<BlockHeader>(), Arg.Any<CancellationToken>());
    }
}
