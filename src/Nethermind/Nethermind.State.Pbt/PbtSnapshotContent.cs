// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Pbt;
using IResettable = Nethermind.Core.Resettables.IResettable;

namespace Nethermind.State.Pbt;

/// <summary>
/// One diff layer of state changes: the complete post-change leaf blobs and stem trie nodes produced
/// by the block's root computation.
/// </summary>
/// <remarks>
/// A blob is the whole of its stem, so a layer holding one has the final say on every account and slot
/// that stem carries — which is why the accounts and slots themselves are not stored a second time (see
/// <see cref="PbtLeafDecoder"/>). Both maps are concurrent because the root fold writes them from as
/// many threads as it runs across. A present null blob means the stem was deleted, and a present null
/// node means the node was removed.
/// <para>
/// Pooled per <see cref="PbtResourcePool.Usage"/>, so an instance backs exactly one layer at a time
/// and must not be touched once returned. Each non-null entry is owned through one lease, which is
/// released when the entry is replaced or the content is reset.
/// </para>
/// </remarks>
public sealed class PbtSnapshotContent : IDisposable, IResettable
{
    private readonly Partition[] _partitions = [new(), new(), new()];

    /// <summary>The tree writes of one independently folded partition.</summary>
    internal Partition this[PbtPartition partition] => _partitions[(int)partition];

    /// <summary>Every partition's tree writes, for a consumer that walks the complete layer.</summary>
    internal ReadOnlySpan<Partition> Partitions => _partitions;

    internal sealed class Partition
    {
        internal readonly ConcurrentDictionary<Stem, RefCountingMemory?> LeafBlobs = new();
        internal readonly ConcurrentDictionary<TrieNodeKey, RefCountingMemory?> TrieNodes = new();
    }

    /// <summary>Stores a transferred lease on a complete stem blob; null marks the stem deleted.</summary>
    public void SetLeafBlob(in Stem stem, RefCountingMemory? blob) =>
        SetOwned(this[PbtPartitions.Of(stem)].LeafBlobs, stem, blob);

    /// <summary>Stores a transferred lease on a trie node; null marks the node removed.</summary>
    public void SetTrieNode(in TrieNodeKey key, RefCountingMemory? node) =>
        SetOwned(this[PbtPartitions.Of(key)].TrieNodes, key, node);

    /// <summary>Returns whether this layer contains the stem and acquires a lease on a non-null blob.</summary>
    public bool TryGetLeafBlob(in Stem stem, out RefCountingMemory? blob) =>
        TryGetLeased(this[PbtPartitions.Of(stem)].LeafBlobs, stem, out blob);

    /// <summary>Returns whether this layer contains the key and acquires a lease on a non-null node.</summary>
    public bool TryGetTrieNode(in TrieNodeKey key, out RefCountingMemory? node) =>
        TryGetLeased(this[PbtPartitions.Of(key)].TrieNodes, key, out node);

    private static void SetOwned<TKey>(ConcurrentDictionary<TKey, RefCountingMemory?> values, TKey key, RefCountingMemory? value)
        where TKey : notnull
    {
        try
        {
            while (true)
            {
                if (values.TryGetValue(key, out RefCountingMemory? previous))
                {
                    if (!values.TryUpdate(key, value, previous)) continue;

                    Release(previous);
                    return;
                }

                if (values.TryAdd(key, value)) return;
            }
        }
        catch
        {
            Release(value);
            throw;
        }
    }

    private static bool TryGetLeased<TKey>(ConcurrentDictionary<TKey, RefCountingMemory?> values, TKey key, out RefCountingMemory? value)
        where TKey : notnull
    {
        while (values.TryGetValue(key, out value))
        {
            if (value is null)
            {
                if (values.TryGetValue(key, out RefCountingMemory? current) && current is null) return true;
                continue;
            }

            try
            {
                value.AcquireLease();
                if (values.TryGetValue(key, out RefCountingMemory? current) && ReferenceEquals(value, current)) return true;
                Release(value);
            }
            catch (InvalidOperationException)
            {
                // A concurrent replacement released the dictionary's lease before this reader could
                // acquire its own. Retry against the replacement rather than touching released bytes.
            }
        }

        value = null;
        return false;
    }

    /// <remarks>
    /// The lock-free clears are sound only at a pool-return boundary, where the layer's last lease has
    /// dropped and the fold that populates the maps has been joined.
    /// </remarks>
    public void Reset()
    {
        foreach (Partition partition in _partitions)
        {
            foreach ((_, RefCountingMemory? blob) in partition.LeafBlobs) Release(blob);
            foreach ((_, RefCountingMemory? node) in partition.TrieNodes) Release(node);

            partition.LeafBlobs.NoLockClear();
            partition.TrieNodes.NoLockClear();
        }
    }

    private static void Release(RefCountingMemory? memory) => ((IDisposable?)memory)?.Dispose();

    /// <remarks>Releases all retained leases when the pool has no room to retain this content.</remarks>
    public void Dispose() => Reset();
}
