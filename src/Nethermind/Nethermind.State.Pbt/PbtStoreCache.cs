// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt;

/// <summary>Caches PBT leaf blobs and trie-node groups across processing scopes.</summary>
/// <remarks>
/// The six value-kind/partition buckets have independent byte budgets. A cached value is valid only
/// for the hash supplied by its caller, which prevents a value from one fork being served to another.
/// Every retained value and every returned value owns a separate <see cref="RefCountingMemory"/> lease.
/// </remarks>
/// <param name="config">The independent byte budget of each cache bucket.</param>
public sealed class PbtStoreCache(IPbtConfig config) : IDisposable
{
    private readonly Bucket<Stem>[] _leafBlobs =
    [
        new(config.AccountLeafBlobCacheSizeBudget),
        new(config.CodeLeafBlobCacheSizeBudget),
        new(config.StorageLeafBlobCacheSizeBudget),
    ];
    private readonly Bucket<TrieNodeKey>[] _trieNodes =
    [
        new(config.AccountTrieNodeCacheSizeBudget),
        new(config.CodeTrieNodeCacheSizeBudget),
        new(config.StorageTrieNodeCacheSizeBudget),
    ];

    /// <summary>Returns a caller-owned lease when <paramref name="stem"/> is cached for <paramref name="hash"/>.</summary>
    public RefCountingMemory? GetLeafBlob(in Stem stem, in ValueHash256 hash) =>
        _leafBlobs[(int)PbtPartitions.Of(stem)].Get(stem, hash);

    /// <summary>Retains a cache-owned lease on <paramref name="blob"/> for <paramref name="stem"/> and <paramref name="hash"/>.</summary>
    public void SetLeafBlob(in Stem stem, in ValueHash256 hash, RefCountingMemory blob) =>
        _leafBlobs[(int)PbtPartitions.Of(stem)].Set(stem, hash, blob);

    /// <summary>Returns a caller-owned lease when <paramref name="key"/> is cached for <paramref name="hash"/>.</summary>
    public RefCountingMemory? GetTrieNode(in TrieNodeKey key, in ValueHash256 hash) =>
        _trieNodes[(int)PbtPartitions.Of(key)].Get(key, hash);

    /// <summary>Retains a cache-owned lease on <paramref name="node"/> for <paramref name="key"/> and <paramref name="hash"/>.</summary>
    public void SetTrieNode(in TrieNodeKey key, in ValueHash256 hash, RefCountingMemory node) =>
        _trieNodes[(int)PbtPartitions.Of(key)].Set(key, hash, node);

    /// <summary>Releases every cache-owned lease.</summary>
    public void Dispose()
    {
        for (int i = 0; i < PbtPartitions.Count; i++)
        {
            _leafBlobs[i].Dispose();
            _trieNodes[i].Dispose();
        }
    }

    private sealed class Bucket<TKey>(ulong budget) : IDisposable where TKey : notnull
    {
        private readonly object _lock = new();
        private readonly Dictionary<TKey, LinkedListNode<Entry>> _entries = [];
        private readonly LinkedList<Entry> _recency = [];
        private ulong _retainedBytes;
        private bool _isDisposed;

        public RefCountingMemory? Get(TKey key, in ValueHash256 hash)
        {
            lock (_lock)
            {
                if (_isDisposed || !_entries.TryGetValue(key, out LinkedListNode<Entry>? node) || node.Value.Hash != hash)
                    return null;

                node.Value.Memory.AcquireLease();
                _recency.Remove(node);
                _recency.AddFirst(node);
                return node.Value.Memory;
            }
        }

        public void Set(TKey key, in ValueHash256 hash, RefCountingMemory memory)
        {
            ulong size = (ulong)memory.GetSpan().Length;
            if (budget == 0 || size > budget) return;

            memory.AcquireLease();
            lock (_lock)
            {
                if (_isDisposed)
                {
                    ((IDisposable)memory).Dispose();
                    return;
                }

                if (_entries.TryGetValue(key, out LinkedListNode<Entry>? old)) Remove(old);

                LinkedListNode<Entry> node = new(new Entry(key, hash, memory, size));
                _recency.AddFirst(node);
                _entries.Add(key, node);
                _retainedBytes += size;

                while (_retainedBytes > budget) Remove(_recency.Last!);
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_isDisposed) return;
                _isDisposed = true;

                LinkedListNode<Entry>? node = _recency.First;
                while (node is not null)
                {
                    ((IDisposable)node.Value.Memory).Dispose();
                    node = node.Next;
                }

                _entries.Clear();
                _recency.Clear();
                _retainedBytes = 0;
            }
        }

        private void Remove(LinkedListNode<Entry> node)
        {
            _entries.Remove(node.Value.Key);
            _recency.Remove(node);
            _retainedBytes -= node.Value.Size;
            ((IDisposable)node.Value.Memory).Dispose();
        }

        private sealed record Entry(TKey Key, ValueHash256 Hash, RefCountingMemory Memory, ulong Size);
    }
}
