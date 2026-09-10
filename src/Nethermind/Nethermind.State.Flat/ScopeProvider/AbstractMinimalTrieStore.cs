// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.Extensions.ObjectPool;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NodeBuffer = System.Collections.Generic.List<(Nethermind.Trie.TreePath Path, Nethermind.Trie.TrieNode Node)>;

namespace Nethermind.State.Flat.ScopeProvider;

public abstract class AbstractMinimalTrieStore : IScopedTrieStore
{
    public abstract TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash);

    public abstract byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None);

    public virtual ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) =>
        throw new NotSupportedException("Commit not supported");


    public byte[] LoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
    {
        byte[]? value = TryLoadRlp(path, hash, flags);
        return value ?? throw new TrieNodeException($"Missing trie node. {path}:{hash}", path, hash);
    }

    public virtual ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address) => throw new UnsupportedOperationException("Get trie node resolver not supported");

    public INodeStorage.KeyScheme Scheme => INodeStorage.KeyScheme.HalfPath;

    public abstract class AbstractMinimalCommitter(ConcurrencyController quota) : ICommitter
    {
        private const int InitialNodeBufferCapacity = 128;
        // A large block's parallel commit fills more buffers than the default pool retains, so the
        // dropped ones re-grew through LOH-sized doublings on every block; retain enough of them,
        // bounded per buffer so an outlier block cannot pin memory.
        private const int MaxRetainedNodeBuffers = 256;
        private const int MaxRetainedNodeBufferCapacity = 1 << 16;
        private static readonly ObjectPool<NodeBuffer> NodeBufferPool =
            new DefaultObjectPool<NodeBuffer>(new NodeBufferPoolPolicy(), MaxRetainedNodeBuffers);

        private ThreadLocal<NodeBuffer>? _parallelBuffers;

        public TrieNode CommitNode(ref TreePath path, TrieNode node)
        {
            ThreadLocal<NodeBuffer>? parallelBuffers = Volatile.Read(ref _parallelBuffers);
            if (parallelBuffers is null)
            {
                WriteNode(path, node);
            }
            else
            {
                parallelBuffers.Value!.Add((path, node));
            }
            return node;
        }

        protected abstract void WriteNode(in TreePath path, TrieNode node);

        protected abstract void PublishNodes(IEnumerable<List<(TreePath Path, TrieNode Node)>> buffers);

        public void Dispose()
        {
            ThreadLocal<NodeBuffer>? parallelBuffers = _parallelBuffers;
            if (parallelBuffers is null) return;

            IList<NodeBuffer> buffers = parallelBuffers.Values;
            try
            {
                PublishNodes(buffers);
            }
            finally
            {
                try
                {
                    parallelBuffers.Dispose();
                }
                finally
                {
                    foreach (NodeBuffer buffer in buffers) NodeBufferPool.Return(buffer);
                }
            }
        }

        bool ICommitter.TryRequestConcurrentQuota()
        {
            if (!quota.TryRequestConcurrencyQuota()) return false;

            if (Volatile.Read(ref _parallelBuffers) is null)
            {
                ThreadLocal<NodeBuffer> candidate = new(static () => NodeBufferPool.Get(), trackAllValues: true);
                ThreadLocal<NodeBuffer>? existing =
                    Interlocked.CompareExchange(ref _parallelBuffers, candidate, null);
                if (existing is not null) candidate.Dispose();
            }

            return true;
        }

        void ICommitter.ReturnConcurrencyQuota() => quota.ReturnConcurrencyQuota();

        private sealed class NodeBufferPoolPolicy : IPooledObjectPolicy<NodeBuffer>
        {
            public NodeBuffer Create() => new(InitialNodeBufferCapacity);

            public bool Return(NodeBuffer buffer)
            {
                if (buffer.Capacity > MaxRetainedNodeBufferCapacity) return false;

                buffer.Clear();
                return true;
            }
        }
    }

    public class UnsupportedOperationException(string message) : Exception(message);
}
