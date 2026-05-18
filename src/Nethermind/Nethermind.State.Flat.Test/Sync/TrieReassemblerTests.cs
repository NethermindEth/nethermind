// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Sync;
using Nethermind.State.Flat.Sync.Snap;
using Nethermind.State.Snap;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Sync;

/// <summary>
/// Verifies that <see cref="TrieReassembler"/> can rebuild the upper missing slice of a
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
        TrieReassembler reassembler = new(_persistence, LimboLogs.Instance);
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
        TrieReassembler reassembler = new(_persistence, LimboLogs.Instance);
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

        TrieReassembler reassembler = new(_persistence, LimboLogs.Instance);
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

        TrieReassembler reassembler = new(_persistence, LimboLogs.Instance);
        Hash256? reassembled = reassembler.ReassembleStorageTrie(accountHash.ValueHash256);

        Assert.That(reassembled, Is.EqualTo(originalRoot));
    }

    /// <summary>
    /// Same as above but only for the storage trie. We commit a storage trie under a single
    /// account, drop the top nodes, and confirm <see cref="TrieReassembler.ReassembleStorageTrie"/>
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

        TrieReassembler reassembler = new(_persistence, LimboLogs.Instance);
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
}
