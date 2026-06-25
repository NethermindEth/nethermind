// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

[Parallelizable(ParallelScope.All)]
public class RawTrieStoreTests
{
    [TestCase(WriteFlags.None, new[] { 16385 })]
    [TestCase(WriteFlags.DisableWAL, new[] { 8192, 8192, 1 })]
    public void Raw_scoped_committer_splits_only_disable_wal_batches(WriteFlags writeFlags, int[] expectedBatchSizes)
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

        Assert.That(nodeStorage.DisposedBatchSizes, Is.EqualTo(expectedBatchSizes));
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

    private sealed class CountingNodeStorage : INodeStorage
    {
        public List<int> DisposedBatchSizes { get; } = [];

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
}
