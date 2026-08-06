// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Sync;
using Nethermind.State.Flat.Sync.Snap;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NUnit.Framework;
using static Nethermind.Trie.PatriciaTree;

namespace Nethermind.State.Flat.Test.Sync;

[TestFixture]
public class TrieReassemblerTests
{

    private SnapshotableMemColumnsDb<FlatDbColumns> _columnsDb = null!;
    private RocksDbPersistence _persistence = null!;
    private TrieReassembler _reassembler = null!;


    [SetUp]
    public void SetUp()
    {
        _columnsDb = new SnapshotableMemColumnsDb<FlatDbColumns>();
        _persistence = new RocksDbPersistence(_columnsDb, LimboLogs.Instance);
        _reassembler = new TrieReassembler(_persistence, LimboLogs.Instance);
    }


    [TearDown]
    public void TearDown() => _columnsDb.Dispose();



    [Test]
    public void Reassembles_state_root_from_single_account()
    {
        Hash256 stateTreeRoot = BuildStateTree(new[]
        {
            (TestItem.KeccakA, new Account(0, 100, Keccak.EmptyTreeHash, Keccak.OfAnEmptyString)),
        });

        DeleteTrieNodes();

        Hash256? reassembledRoot = _reassembler.TryReassemble([]);

        Assert.That(reassembledRoot, Is.EqualTo(stateTreeRoot));
    }


    [Test]
    public void Reassembles_storage_root_from_single_slot()
    {
        Hash256 stateTreeRoot = BuildStateTreeWithStorage(new[]
        {
            (TestItem.KeccakA, new byte[] { 0x01, 0x02, 0x03 }),
        });

        DeleteTrieNodes();

        Hash256? reassembledRoot = _reassembler.TryReassemble(new[] { TestItem.KeccakA });
        Assert.That(reassembledRoot, Is.EqualTo(stateTreeRoot));
    }

    [Test]
    public void Reassembles_state_root_from_multiple_accounts()
    {
        Hash256 stateTreeRoot = BuildStateTree(new[]
        {
            (TestItem.KeccakA, new Account(0, 100, Keccak.EmptyTreeHash, Keccak.OfAnEmptyString)),
            (TestItem.KeccakB, new Account(0, 200, Keccak.EmptyTreeHash, Keccak.OfAnEmptyString)),
            (TestItem.KeccakC, new Account(0, 300, Keccak.EmptyTreeHash, Keccak.OfAnEmptyString)),
            (TestItem.KeccakD, new Account(0, 400, Keccak.EmptyTreeHash, Keccak.OfAnEmptyString)),
            (TestItem.KeccakE, new Account(0, 500, Keccak.EmptyTreeHash, Keccak.OfAnEmptyString)),
            (TestItem.KeccakF, new Account(0, 600, Keccak.EmptyTreeHash, Keccak.OfAnEmptyString)),
        });

        DeleteTrieNodes();

        Hash256? reassembledRoot = _reassembler.TryReassemble([]);
        Assert.That(reassembledRoot, Is.EqualTo(stateTreeRoot));
    }


    [Test]
    public void Reassembles_storage_root_from_multiple_slots()
    {
        Hash256 stateTreeRoot = BuildStateTreeWithStorage(new[]
        {
            (TestItem.KeccakA, new byte[] { 0x01, 0x02, 0x03 }),
            (TestItem.KeccakB, new byte[] { 0x04, 0x05, 0x06 }),
            (TestItem.KeccakC, new byte[] { 0x07, 0x08, 0x09 }),
            (TestItem.KeccakD, new byte[] { 0x0A, 0x0B, 0x0C }),
            (TestItem.KeccakE, new byte[] { 0x0D, 0x0E, 0x0F }),
            (TestItem.KeccakF, new byte[] { 0x10, 0x11, 0x12 }),
        });

        DeleteTrieNodes();

        Hash256? reassembledRoot = _reassembler.TryReassemble([]);
        Assert.That(reassembledRoot, Is.EqualTo(stateTreeRoot));
    }


    [Test]
    public void Reassembles_storage_root_with_inline_nodes()
    {
        Hash256 stateTreeRoot = BuildStateTreeWithStorage(new[]
        {
            (new Hash256("ab".PadRight(63, '0') + "1"), new byte[] { 0x03}),
            (new Hash256("ab".PadRight(63, '0') + "2"), new byte[] { 0x04}),
        });

        DeleteTrieNodes();

        Hash256? reassembledRoot = _reassembler.TryReassemble([]);
        Assert.That(reassembledRoot, Is.EqualTo(stateTreeRoot));
    }


    [Test]
    public void Reassembles_storage_root_for_updated_account()
    {
        Hash256 correctStorageRootA = BuildStorageTree(TestItem.KeccakA, new[]
        {
            (TestItem.KeccakB, new byte[] { 0x01, 0x02, 0x03 }),
        });
        Hash256 correctStorageRootC = BuildStorageTree(TestItem.KeccakC, new[]
        {
            (TestItem.KeccakD, new byte[] { 0x01, 0x02, 0x03 }),
        });
        Hash256 correctStateRoot = BuildStateTree(new[]
        {
            (TestItem.KeccakA, new Account(0, 100, correctStorageRootA, Keccak.OfAnEmptyString)),
            (TestItem.KeccakC, new Account(0, 100, correctStorageRootC, Keccak.OfAnEmptyString)),
        });

        ClearAll();

        BuildStorageTree(TestItem.KeccakA, new[]
        {
            (TestItem.KeccakB, new byte[] { 0x01, 0x02, 0x03 }),
        });
        BuildStorageTree(TestItem.KeccakC, new[]
        {
            (TestItem.KeccakD, new byte[] { 0x01, 0x02, 0x03 }),
        });
        Hash256 staleStateRoot = BuildStateTree(new[]
        {
            (TestItem.KeccakA, new Account(0, 100, Keccak.EmptyTreeHash, Keccak.OfAnEmptyString)),
            (TestItem.KeccakC, new Account(0, 100, Keccak.EmptyTreeHash, Keccak.OfAnEmptyString)),
        });

        DeleteTrieNodes();

        Hash256? reassembledRoot = _reassembler.TryReassemble([TestItem.KeccakA, TestItem.KeccakC]);
        Assert.That(reassembledRoot, Is.Not.EqualTo(staleStateRoot));
        Assert.That(reassembledRoot, Is.EqualTo(correctStateRoot));
    }


    [Test]
    public void Reassembles_state_with_stale_storage_when_updated_account_is_missing()
    {
        Hash256 correctStorageRoot = BuildStorageTree(TestItem.KeccakA, new[]
        {
            (TestItem.KeccakB, new byte[] { 0x01, 0x02, 0x03 }),
        });
        Hash256 correctStateRoot = BuildStateTree(new[]
        {
            (TestItem.KeccakA, new Account(0, 100, correctStorageRoot, Keccak.OfAnEmptyString)),
        });

        ClearAll();

        Hash256 _ = BuildStorageTree(TestItem.KeccakA, new[]
        {
            (TestItem.KeccakB, new byte[] { 0x01, 0x02, 0x03 }),
        });
        Hash256 staleStateRoot = BuildStateTree(new[]
        {
            (TestItem.KeccakA, new Account(0, 100, Keccak.EmptyTreeHash, Keccak.OfAnEmptyString)),
        });

        DeleteTrieNodes();

        Hash256? reassembledRoot = _reassembler.TryReassemble([]);
        Assert.That(reassembledRoot, Is.EqualTo(staleStateRoot));
        Assert.That(reassembledRoot, Is.Not.EqualTo(correctStateRoot));
    }

    [Test]
    public void Reassembles_state_with_multiple_accounts_only_one_updated()
    {
        Hash256 expectedStorageRoot = BuildStorageTree(TestItem.KeccakA, new[]
        {
            (TestItem.KeccakB, new byte[] { 0x01, 0x02, 0x03 }),
            (TestItem.KeccakC, new byte[] { 0x04, 0x05, 0x06 }),
        });
        Hash256 expectedStateRoot = BuildStateTree(new[]
        {
            (TestItem.KeccakA, new Account(0, 100, expectedStorageRoot, Keccak.OfAnEmptyString)),
            (TestItem.KeccakD, new Account(0, 200, Keccak.EmptyTreeHash, Keccak.OfAnEmptyString)),
            (TestItem.KeccakE, new Account(0, 300, Keccak.EmptyTreeHash, Keccak.OfAnEmptyString)),
        });

        DeleteTrieNodes();

        Hash256? reassembledRoot = _reassembler.TryReassemble([TestItem.KeccakA]);
        Assert.That(reassembledRoot, Is.EqualTo(expectedStateRoot));
    }

    [Test]
    public void Reassembles_state_rebuilds_subtree_when_rewrites_present_despite_existing_node()
    {
        // Step 1: compute expected root
        Hash256 newStorageRoot = BuildStorageTree(TestItem.KeccakA, new[]
        {
            (TestItem.KeccakB, new byte[] { 0x01, 0x02, 0x03 }),
            (TestItem.KeccakC, new byte[] { 0x04, 0x05, 0x06 }),
        });
        Hash256 expectedStateRoot = BuildStateTree(new[]
        {
            (TestItem.KeccakA, new Account(0, 100, newStorageRoot, Keccak.OfAnEmptyString)),
            (TestItem.KeccakD, new Account(0, 200, Keccak.EmptyTreeHash, Keccak.OfAnEmptyString)),
        });

        ClearAll();

        // Step 2: overwrite with stale state trie (A has old storage root baked in)
        BuildStateTree(new[]
        {
            (TestItem.KeccakA, new Account(0, 100, Keccak.EmptyTreeHash, Keccak.OfAnEmptyString)),
            (TestItem.KeccakD, new Account(0, 200, Keccak.EmptyTreeHash, Keccak.OfAnEmptyString)),
        });

        // Step 3: restore storage trie to new data (state trie nodes remain stale)
        BuildStorageTree(TestItem.KeccakA, new[]
        {
            (TestItem.KeccakB, new byte[] { 0x01, 0x02, 0x03 }),
            (TestItem.KeccakC, new byte[] { 0x04, 0x05, 0x06 }),
        });

        DeleteTrieNodes();

        // Step 4: patch flat account entry only (state trie leaves remain stale)
        IPersistence.IWriteBatch writer = _persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL);
        writer.SetAccountRaw(TestItem.KeccakA, new Account(0, 100, newStorageRoot, Keccak.OfAnEmptyString));
        writer.Dispose();

        // State trie branch is stale but exists — reassembler must ignore it and rebuild
        Hash256? reassembledRoot = _reassembler.TryReassemble([TestItem.KeccakA]);
        Assert.That(reassembledRoot, Is.EqualTo(expectedStateRoot));
    }

    [Test]
    public void Returns_no_root_when_existing_trie_node_is_corrupt()
    {
        BuildStateTree(new[]
        {
            (TestItem.KeccakA, new Account(0, 100, Keccak.EmptyTreeHash, Keccak.OfAnEmptyString)),
            (TestItem.KeccakB, new Account(0, 200, Keccak.EmptyTreeHash, Keccak.OfAnEmptyString)),
        });


        // corrupt nodes
        foreach (FlatDbColumns col in new[] { FlatDbColumns.StateTopNodes, FlatDbColumns.StateNodes })
        {
            IDb db = _columnsDb.GetColumnDb(col);
            foreach (KeyValuePair<byte[], byte[]?> kvp in db.GetAll())
            {
                if (kvp.Value is null) continue;
                TrieNode node = new(NodeType.Unknown, kvp.Value);
                node.ResolveNode(NullTrieNodeResolver.Instance, TreePath.Empty);
                if (node.NodeType != NodeType.Leaf)
                {
                    db[kvp.Key] = [0x12];
                }
            }
        }
        Hash256? result = _reassembler.TryReassemble([TestItem.KeccakA, TestItem.KeccakB]);
        Assert.That(result, Is.Null);
    }

    [Test]
    public void Returns_no_root_when_state_is_empty()
    {
        Hash256? result = _reassembler.TryReassemble([]);
        Assert.That(result, Is.Null);
    }

    private Hash256 BuildStateTree(IEnumerable<(Hash256 path, Account account)> accounts)
    {
        using IPersistence.IPersistenceReader reader = _persistence.CreateReader(ReaderFlags.Sync);
        using IPersistence.IWriteBatch writeBatch = _persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL);
        StateTree tree = new(new PersistenceTrieStoreAdapter(reader, writeBatch, enableDoubleWriteCheck: false), LimboLogs.Instance);

        ArrayPoolListRef<BulkSetEntry> bulkSetEntries = new();
        foreach ((Hash256 path, Account account) in accounts)
        {
            Rlp rlp = account.IsTotallyEmpty ? StateTree.EmptyAccountRlp : AccountDecoder.Instance.Encode(account);
            bulkSetEntries.Add(new BulkSetEntry(path, rlp.Bytes));
            writeBatch.SetAccountRaw(path.ValueHash256, account);
        }

        tree.BulkSet(bulkSetEntries);
        tree.Commit();
        tree.UpdateRootHash();

        return tree.RootHash;
    }

    private Hash256 BuildStorageTree(Hash256 addressHash, IEnumerable<(Hash256 path, byte[] value)> slots)
    {
        using IPersistence.IPersistenceReader reader = _persistence.CreateReader(ReaderFlags.Sync);
        using IPersistence.IWriteBatch writeBatch = _persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL);
        StorageTree tree = new(new PersistenceStorageTrieStoreAdapter(reader, writeBatch, addressHash, enableDoubleWriteCheck: false), LimboLogs.Instance);

        ArrayPoolListRef<BulkSetEntry> bulkSetEntries = new();
        foreach ((Hash256 path, byte[] value) in slots)
        {
            bulkSetEntries.Add(new BulkSetEntry(path, value));
            writeBatch.SetStorageRawEncoded(addressHash, path, Rlp.Encode(value).Bytes);
        }

        tree.BulkSet(bulkSetEntries);
        tree.Commit();
        tree.UpdateRootHash();

        return tree.RootHash;
    }

    private Hash256 BuildStateTreeWithStorage(IEnumerable<(Hash256 path, byte[] value)> slots)
    {
        Hash256 storageRoot = BuildStorageTree(TestItem.KeccakA, slots);
        Account account = new(0, 100, storageRoot, Keccak.OfAnEmptyString);
        return BuildStateTree(new[] { (TestItem.KeccakA, account) });
    }


    private void ClearAll()
    {
        foreach (FlatDbColumns col in Enum.GetValues<FlatDbColumns>())
        {
            IDb db = _columnsDb.GetColumnDb(col);
            db.Clear();
        }
    }

    // Delete all trie nodes that pass the filter
    // If no filter is provided, all nodes are deleted
    // State tree leaves always kept
    private void DeleteTrieNodes(Func<TrieNode, bool>? filter = null)
    {
        DeleteStateNodes(filter);
        DeleteStorageNodes(filter);
    }

    private void DeleteStateNodes(Func<TrieNode, bool>? filter = null)
    {
        filter ??= static _ => true;

        foreach (FlatDbColumns col in new[] { FlatDbColumns.StateTopNodes, FlatDbColumns.StateNodes })
        {
            IDb columnDb = _columnsDb.GetColumnDb(col);
            List<byte[]> toDelete = [];
            foreach (KeyValuePair<byte[], byte[]?> kvp in columnDb.GetAll())
            {
                if (kvp.Value is null) continue;
                TrieNode node = new(NodeType.Unknown, kvp.Value);
                node.ResolveNode(NullTrieNodeResolver.Instance, TreePath.Empty);
                if (node.NodeType != NodeType.Leaf && filter(node))
                    toDelete.Add(kvp.Key);
            }
            foreach (byte[] key in toDelete)
                columnDb.Remove(key);
        }

        {
            IDb fallbackDb = _columnsDb.GetColumnDb(FlatDbColumns.FallbackNodes);
            List<byte[]> toDelete = [];
            foreach (KeyValuePair<byte[], byte[]?> kvp in fallbackDb.GetAll())
            {
                if (kvp.Value is null) continue;
                TrieNode node = new(NodeType.Unknown, kvp.Value);
                node.ResolveNode(NullTrieNodeResolver.Instance, TreePath.Empty);

                // state node
                if (kvp.Key[0] == 0 && node.NodeType != NodeType.Leaf && filter(node))
                    toDelete.Add(kvp.Key);
            }
            foreach (byte[] key in toDelete)
                fallbackDb.Remove(key);
        }
    }


    private void DeleteStorageNodes(Func<TrieNode, bool>? filter = null)
    {
        filter ??= static _ => true;

        {
            IDb columnDb = _columnsDb.GetColumnDb(FlatDbColumns.StorageNodes);
            List<byte[]> toDelete = [];
            foreach (KeyValuePair<byte[], byte[]?> kvp in columnDb.GetAll())
            {
                if (kvp.Value is null) continue;
                TrieNode node = new(NodeType.Unknown, kvp.Value);
                node.ResolveNode(NullTrieNodeResolver.Instance, TreePath.Empty);
                if (filter(node))
                    toDelete.Add(kvp.Key);
            }
            foreach (byte[] key in toDelete)
                columnDb.Remove(key);
        }

        {
            IDb fallbackDb = _columnsDb.GetColumnDb(FlatDbColumns.FallbackNodes);
            List<byte[]> toDelete = [];
            foreach (KeyValuePair<byte[], byte[]?> kvp in fallbackDb.GetAll())
            {
                if (kvp.Value is null) continue;
                TrieNode node = new(NodeType.Unknown, kvp.Value);
                node.ResolveNode(NullTrieNodeResolver.Instance, TreePath.Empty);

                // storage node
                if (kvp.Key[0] == 1 && filter(node))
                    toDelete.Add(kvp.Key);
            }
            foreach (byte[] key in toDelete)
                fallbackDb.Remove(key);
        }
    }

}
