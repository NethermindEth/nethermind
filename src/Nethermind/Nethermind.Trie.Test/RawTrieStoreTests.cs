// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using Nethermind.Trie.Utils;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

[Parallelizable(ParallelScope.All)]
public class RawTrieStoreTests
{
    [TestCase(WriteFlags.None, 1, 16_385, 16_385)]
    [TestCase(WriteFlags.DisableWAL, 9, TrieWriteBatchSettings.DefaultDisableWalBatchSize, 1)]
    public void Raw_scoped_committer_splits_only_disable_wal_batches(WriteFlags writeFlags, int expectedBatchCount, int expectedMaxBatchSize, int expectedLastBatchSize)
    {
        CountingNodeStorage nodeStorage = new();
        using (ICommitter committer = new RawScopedTrieStore.Committer(nodeStorage, null, writeFlags))
        {
            TreePath path = TreePath.Empty;
            for (int i = 0; i < 16385; i++)
            {
                committer.CommitNode(ref path, CreateNode(i));
            }
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(nodeStorage.DisposedBatchSizes, Has.Count.EqualTo(expectedBatchCount));
            Assert.That(nodeStorage.DisposedBatchSizes, Has.All.LessThanOrEqualTo(expectedMaxBatchSize));
            Assert.That(nodeStorage.DisposedBatchSizes[^1], Is.EqualTo(expectedLastBatchSize));
        }
    }

    [Test]
    public void Raw_scoped_committer_uses_configured_disable_wal_batch_size()
    {
        const int batchSize = 4096;
        CountingNodeStorage nodeStorage = new();
        using (ICommitter committer = new RawScopedTrieStore.Committer(nodeStorage, null, WriteFlags.DisableWAL, disableWalBatchSize: batchSize))
        {
            TreePath path = TreePath.Empty;
            for (int i = 0; i < batchSize + 1; i++)
            {
                committer.CommitNode(ref path, CreateNode(i));
            }
        }

        Assert.That(nodeStorage.DisposedBatchSizes, Is.EqualTo(new[] { batchSize, 1 }));
    }

    [Test]
    public void Raw_scoped_committer_replays_disable_wal_hash_writes_in_global_key_order()
    {
        const int batchSize = 2;
        CountingNodeStorage nodeStorage = new();
        ValueHash256 hash1 = new("0000000000000000000000000000000000000000000000000000000000000001");
        ValueHash256 hash2 = new("0000000000000000000000000000000000000000000000000000000000000002");
        ValueHash256 hash3 = new("0000000000000000000000000000000000000000000000000000000000000003");
        ValueHash256 hash4 = new("0000000000000000000000000000000000000000000000000000000000000004");
        ValueHash256[] inputHashes = [hash3, hash1, hash4, hash2];

        using (ICommitter committer = new RawScopedTrieStore.Committer(nodeStorage, null, WriteFlags.DisableWAL, disableWalBatchSize: batchSize))
        {
            TreePath path = TreePath.Empty;
            for (int i = 0; i < inputHashes.Length; i++)
            {
                committer.CommitNode(ref path, CreateNode(inputHashes[i], (byte)i));
            }
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(nodeStorage.DisposedBatchSizes, Is.EqualTo(new[] { batchSize, batchSize }));
            Assert.That(GetWrittenHashes(nodeStorage.Writes), Is.EqualTo(new[] { hash1, hash2, hash3, hash4 }));
        }
    }

    [Test]
    public void Raw_scoped_committer_preserves_duplicate_disable_wal_write_order()
    {
        CountingNodeStorage nodeStorage = new();
        ValueHash256 hash = TestItem.KeccakA.ValueHash256;

        using (ICommitter committer = new RawScopedTrieStore.Committer(nodeStorage, null, WriteFlags.DisableWAL, disableWalBatchSize: 1))
        {
            TreePath path = TreePath.Empty;
            committer.CommitNode(ref path, CreateNode(hash, 1));
            committer.CommitNode(ref path, CreateNode(hash, 2));
        }

        Assert.That(GetWrittenData(nodeStorage.Writes), Is.EqualTo(new byte[] { 1, 2 }));
    }

    [Test]
    public void Raw_scoped_committer_replays_disable_wal_half_path_writes_in_storage_key_order()
    {
        const int batchSize = 2;
        CountingNodeStorage nodeStorage = new()
        {
            Scheme = INodeStorage.KeyScheme.HalfPath
        };
        NodeStorageWrite[] inputWrites =
        [
            new(null, TreePath.FromHexString("2000000000000001"), new ValueHash256("0000000000000000000000000000000000000000000000000000000000000003"), [5]),
            new(null, TreePath.FromHexString("1000000000000000"), new ValueHash256("0000000000000000000000000000000000000000000000000000000000000002"), [4]),
            new(null, TreePath.Empty, new ValueHash256("0000000000000000000000000000000000000000000000000000000000000004"), [3]),
            new(null, TreePath.FromHexString("1000000000000000"), new ValueHash256("0000000000000000000000000000000000000000000000000000000000000001"), [2]),
            new(null, TreePath.FromHexString("2000000000000000"), new ValueHash256("0000000000000000000000000000000000000000000000000000000000000005"), [1]),
        ];
        byte[] expectedData = GetNodeStorageKeyOrderedData(INodeStorage.KeyScheme.HalfPath, inputWrites);

        using (ICommitter committer = new RawScopedTrieStore.Committer(nodeStorage, null, WriteFlags.DisableWAL, disableWalBatchSize: batchSize))
        {
            for (int i = 0; i < inputWrites.Length; i++)
            {
                NodeStorageWrite write = inputWrites[i];
                TreePath path = write.Path;
                committer.CommitNode(ref path, CreateNode(write.Hash, write.Data[0]));
            }
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(nodeStorage.DisposedBatchSizes, Is.EqualTo(new[] { batchSize, batchSize, 1 }));
            Assert.That(GetWrittenData(nodeStorage.Writes), Is.EqualTo(expectedData));
        }
    }

    [Test]
    public void Sorted_node_write_batcher_accepts_concurrent_writes_and_replays_in_key_order()
    {
        const int writeCount = 64;
        const int batchSize = 8;
        CountingNodeStorage nodeStorage = new();

        using (SortedNodeWriteBatcher batcher = new(nodeStorage, batchSize))
        {
            Parallel.For(0, writeCount, index =>
            {
                int value = writeCount - index;
                TreePath path = TreePath.Empty;
                batcher.Set(null, path, CreateHash(value), [(byte)value], WriteFlags.DisableWAL);
            });
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(nodeStorage.DisposedBatchSizes, Has.Count.EqualTo(writeCount / batchSize));
            Assert.That(nodeStorage.DisposedBatchSizes, Has.All.EqualTo(batchSize));
            Assert.That(GetWrittenData(nodeStorage.Writes), Is.EqualTo(CreateExpectedAscendingData(writeCount)));
        }
    }

    [Test]
    public void Raw_scoped_committer_does_not_dispose_external_batch()
    {
        CountingNodeStorage nodeStorage = new();
        INodeStorage.IWriteBatch writeBatch = nodeStorage.StartWriteBatch();
        using (ICommitter committer = new RawScopedTrieStore.Committer(nodeStorage, null, WriteFlags.DisableWAL, writeBatch))
        {
            TreePath path = TreePath.Empty;
            for (int i = 0; i < 3; i++)
            {
                committer.CommitNode(ref path, CreateNode(i));
            }
        }

        Assert.That(nodeStorage.DisposedBatchSizes, Is.Empty);

        writeBatch.Dispose();

        Assert.That(nodeStorage.DisposedBatchSizes, Is.EqualTo(new[] { 3 }));
    }

    [TestCase(WriteFlags.None, false)]
    [TestCase(WriteFlags.DisableWAL, true)]
    public void Raw_scoped_committer_grants_concurrency_only_for_owned_disable_wal_batches(WriteFlags writeFlags, bool expected)
    {
        CountingNodeStorage nodeStorage = new();
        using ICommitter committer = new RawScopedTrieStore.Committer(nodeStorage, null, writeFlags);

        Assert.That(committer.TryRequestConcurrentQuota(), Is.EqualTo(expected));
        committer.ReturnConcurrencyQuota();
    }

    [Test]
    public void Raw_scoped_committer_does_not_grant_concurrency_for_external_batch()
    {
        CountingNodeStorage nodeStorage = new();
        using INodeStorage.IWriteBatch writeBatch = nodeStorage.StartWriteBatch();
        using ICommitter committer = new RawScopedTrieStore.Committer(nodeStorage, null, WriteFlags.DisableWAL, writeBatch);

        Assert.That(committer.TryRequestConcurrentQuota(), Is.False);
    }

    [Test]
    public void SmokeTest()
    {
        MemDb db = new();
        PatriciaTree patriciaTree = new(new RawTrieStore(db).GetTrieStore(null), LimboLogs.Instance);

        patriciaTree.Set(TestItem.KeccakA.Bytes, TestItem.KeccakA.BytesToArray());
        patriciaTree.Set(TestItem.KeccakB.Bytes, TestItem.KeccakB.BytesToArray());
        patriciaTree.Set(TestItem.KeccakC.Bytes, TestItem.KeccakC.BytesToArray());

        patriciaTree.Commit();
        Assert.That(patriciaTree.RootHash, Is.Not.EqualTo(Keccak.EmptyTreeHash));

        Hash256 rootHash = patriciaTree.RootHash;

        // Recreate
        patriciaTree = new PatriciaTree(new RawTrieStore(db).GetTrieStore(null), LimboLogs.Instance);
        patriciaTree.RootHash = rootHash;

        Assert.That(patriciaTree.Get(TestItem.KeccakA.Bytes).ToArray(), Is.EqualTo(TestItem.KeccakA.BytesToArray()));
        Assert.That(patriciaTree.Get(TestItem.KeccakB.Bytes).ToArray(), Is.EqualTo(TestItem.KeccakB.BytesToArray()));
        Assert.That(patriciaTree.Get(TestItem.KeccakC.Bytes).ToArray(), Is.EqualTo(TestItem.KeccakC.BytesToArray()));
    }

    private static TrieNode CreateNode(int seed)
    {
        byte[] rlp = [(byte)seed, (byte)(seed >> 8), (byte)(seed >> 16), (byte)(seed >> 24)];
        return new TrieNode(NodeType.Unknown, Keccak.Compute(rlp), rlp);
    }

    private static TrieNode CreateNode(ValueHash256 hash, byte data) =>
        new(NodeType.Unknown, new Hash256(hash), [data]);

    private static ValueHash256 CreateHash(int value)
    {
        byte[] bytes = new byte[32];
        bytes[^1] = (byte)value;
        return new ValueHash256(bytes);
    }

    private static byte[] CreateExpectedAscendingData(int count)
    {
        byte[] data = new byte[count];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i + 1);
        }

        return data;
    }

    private static ValueHash256[] GetWrittenHashes(List<NodeStorageWrite> writes)
    {
        ValueHash256[] hashes = new ValueHash256[writes.Count];
        for (int i = 0; i < writes.Count; i++)
        {
            hashes[i] = writes[i].Hash;
        }

        return hashes;
    }

    private static byte[] GetWrittenData(List<NodeStorageWrite> writes)
    {
        byte[] data = new byte[writes.Count];
        for (int i = 0; i < writes.Count; i++)
        {
            data[i] = writes[i].Data[0];
        }

        return data;
    }

    private static byte[] GetNodeStorageKeyOrderedData(INodeStorage.KeyScheme scheme, NodeStorageWrite[] writes)
    {
        NodeStorageWrite[] orderedWrites = [.. writes];
        Array.Sort(orderedWrites, (x, y) => CompareByNodeStorageKey(scheme, x, y));

        byte[] data = new byte[orderedWrites.Length];
        for (int i = 0; i < orderedWrites.Length; i++)
        {
            data[i] = orderedWrites[i].Data[0];
        }

        return data;
    }

    private static int CompareByNodeStorageKey(INodeStorage.KeyScheme scheme, NodeStorageWrite x, NodeStorageWrite y)
    {
        ValueHash256? xAddress = x.Address;
        ValueHash256? yAddress = y.Address;
        byte[] xKey = NodeStorage.GetNodeStoragePath(scheme, xAddress, x.Path, x.Hash);
        byte[] yKey = NodeStorage.GetNodeStoragePath(scheme, yAddress, y.Path, y.Hash);
        return xKey.AsSpan().SequenceCompareTo(yKey);
    }

    private sealed class CountingNodeStorage : INodeStorage
    {
        public List<int> DisposedBatchSizes { get; } = [];

        public List<NodeStorageWrite> Writes { get; } = [];

        public INodeStorage.KeyScheme Scheme { get; set; } = INodeStorage.KeyScheme.Hash;

        public bool RequirePath => false;

        public byte[]? Get(Hash256? address, in TreePath path, in ValueHash256 keccak, ReadFlags readFlags = ReadFlags.None) => null;

        public void Set(Hash256? address, in TreePath path, in ValueHash256 hash, ReadOnlySpan<byte> data, WriteFlags writeFlags = WriteFlags.None) { }

        public INodeStorage.IWriteBatch StartWriteBatch() => new CountingWriteBatch(this);

        public bool KeyExists(in ValueHash256? address, in TreePath path, in ValueHash256 hash) => false;

        public void Flush(bool onlyWal) { }

        public void Compact() { }

        private sealed class CountingWriteBatch(CountingNodeStorage nodeStorage) : INodeStorage.IWriteBatch
        {
            private int _count;
            private bool _disposed;

            public void Set(Hash256? address, in TreePath path, in ValueHash256 currentNodeKeccak, ReadOnlySpan<byte> data, WriteFlags writeFlags)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _count++;
                nodeStorage.Writes.Add(new NodeStorageWrite(address, path, currentNodeKeccak, data.ToArray()));
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                nodeStorage.DisposedBatchSizes.Add(_count);
            }
        }
    }

    private readonly record struct NodeStorageWrite(Hash256? Address, TreePath Path, ValueHash256 Hash, byte[] Data);
}
