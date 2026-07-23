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
    private readonly ConcurrentDictionary<Stem, RefCountingMemory?> _leafBlobs = new();
    private readonly ConcurrentDictionary<TrieNodeKey, RefCountingMemory?> _trieNodes = new();

    internal IReadOnlyDictionary<Stem, RefCountingMemory?> LeafBlobs => _leafBlobs;
    internal IReadOnlyDictionary<TrieNodeKey, RefCountingMemory?> TrieNodes => _trieNodes;

    /// <summary>Stores a transferred lease on a complete stem blob; null marks the stem deleted.</summary>
    public void SetLeafBlob(in Stem stem, RefCountingMemory? blob) => SetOwned(_leafBlobs, stem, blob);

    /// <summary>Stores a transferred lease on a trie node; null marks the node removed.</summary>
    public void SetTrieNode(in TrieNodeKey key, RefCountingMemory? node) => SetOwned(_trieNodes, key, node);

    /// <summary>Returns whether this layer contains the stem and acquires a lease on a non-null blob.</summary>
    public bool TryGetLeafBlob(in Stem stem, out RefCountingMemory? blob) => TryGetLeased(_leafBlobs, stem, out blob);

    /// <summary>Returns whether this layer contains the key and acquires a lease on a non-null node.</summary>
    public bool TryGetTrieNode(in TrieNodeKey key, out RefCountingMemory? node) => TryGetLeased(_trieNodes, key, out node);

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
        foreach ((_, RefCountingMemory? blob) in _leafBlobs) Release(blob);
        foreach ((_, RefCountingMemory? node) in _trieNodes) Release(node);

        _leafBlobs.NoLockClear();
        _trieNodes.NoLockClear();
    }

    private static void Release(RefCountingMemory? memory) => ((IDisposable?)memory)?.Dispose();

    /// <remarks>Releases all retained leases when the pool has no room to retain this content.</remarks>
    public void Dispose() => Reset();
}
