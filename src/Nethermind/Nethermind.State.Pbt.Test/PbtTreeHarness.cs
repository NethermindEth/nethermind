// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;
using Nethermind.Pbt.Tiles;

namespace Nethermind.State.Pbt.Test;

/// <summary>
/// Drives <see cref="TrieUpdater.UpdateRoot"/> the way the production scope does: raw 32-byte
/// key/value writes are packed into a <see cref="PbtWriteBatch"/> and applied over dictionary-backed
/// node/blob stores that persist across batches.
/// </summary>
/// <param name="writeLayout">
/// Which layout the batches write; settable through <see cref="WriteLayout"/> so that one store can
/// be driven across a format switch, as a node whose configuration changed does.
/// </param>
public sealed class PbtTreeHarness(IRefCountingMemoryProvider memoryProvider, PbtTrieLayout writeLayout) : IPbtStore
{
    private readonly Dictionary<TrieNodeKey, byte[]> _nodes = [];
    private readonly Dictionary<Stem, byte[]> _blobs = [];
    private readonly List<RefCountingMemory> _handedOut = [];
    private readonly HashSet<int> _readThreads = [];

    // Parallel folds access all test-store state concurrently.
    private readonly Lock _lock = new();
    private PbtPartitionRoots _roots = PbtPartitionRoots.Empty;

    /// <inheritdoc cref="PbtTreeHarness(IRefCountingMemoryProvider, PbtTrieLayout)" path="/param[@name='writeLayout']"/>
    public PbtTrieLayout WriteLayout { get; set; } = writeLayout;

    /// <summary>
    /// <inheritdoc cref="TrieUpdater.UpdateRoot" path="/param[@name='concurrency']"/> Serial by default,
    /// so that a test measuring what the descent read or wrote sees one thread's worth of it.
    /// </summary>
    public int RootFoldConcurrency { get; set; } = 1;

    /// <summary>The partition roots after the latest batch.</summary>
    public PbtPartitionRoots Roots => _roots;

    /// <summary>The blobs as the store keeps them, a run having no entry of its own.</summary>
    public IReadOnlyDictionary<TrieNodeKey, byte[]> Nodes => _nodes;

    /// <summary>The leaf blobs, one per stem the trie holds — an emptied one is removed, not stored empty.</summary>
    public IReadOnlyDictionary<Stem, byte[]> Blobs => _blobs;

    /// <summary>Every value handed to a reader, to check the leases on them were balanced.</summary>
    public IReadOnlyList<RefCountingMemory> HandedOut => _handedOut;

    /// <summary>Count of <see cref="GetLeafBlob"/> calls, to pin that the updater skips the read for brand-new stems.</summary>
    public int LeafReads { get; private set; }

    /// <summary>Count of <see cref="SetTrieNode"/> calls, to pin which groups a batch really rebuilds.</summary>
    public int NodeWrites { get; private set; }

    /// <summary>
    /// The threads that have read the store, so a test can tell that a fold it asked to run in
    /// parallel really did — an assertion over a fold that quietly stayed on one thread proves nothing.
    /// </summary>
    public int ReadThreadCount
    {
        get
        {
            lock (_lock) return _readThreads.Count;
        }
    }

    /// <summary>Forgets prior read threads so a test can measure one batch in isolation.</summary>
    public void ResetReadThreads()
    {
        lock (_lock) _readThreads.Clear();
    }

    /// <summary>
    /// Every node the store holds, keyed by where it sits in the trie, with runs inside a group flattened
    /// back out to the keys they would have had of their own.
    /// </summary>
    /// <remarks>
    /// What a walk of the trie wants, as against <see cref="Nodes"/>, which is what the store wants: a
    /// blob's key says where its node is, and a run's is its parent's plus its boundary slot.
    /// </remarks>
    public Dictionary<TrieNodeKey, byte[]> FlattenedNodes()
    {
        Dictionary<TrieNodeKey, byte[]> flattened = new(_nodes);
        foreach ((TrieNodeKey key, byte[] blob) in _nodes)
        {
            switch (WriteLayout.Tiling())
            {
                case PbtTiling.FourLevel: FlattenChains<PbtFourLevelTileLayout>(flattened, key, blob); break;
                case PbtTiling.FiveLevel: FlattenChains<PbtFiveLevelTileLayout>(flattened, key, blob); break;
                case PbtTiling.SixLevel: FlattenChains<PbtSixLevelTileLayout>(flattened, key, blob); break;
                case PbtTiling.EightLevel: FlattenChains<PbtEightLevelTileLayout>(flattened, key, blob); break;
                default: throw new ArgumentOutOfRangeException(nameof(WriteLayout));
            }
        }

        return flattened;
    }

    private static void FlattenChains<TLayout>(Dictionary<TrieNodeKey, byte[]> flattened, in TrieNodeKey key, byte[] blob)
        where TLayout : IPbtTileLayout
    {
        if (PbtNodeChain.IsChain(blob)) return;

        PbtTrieNodeGroup<TLayout> group = PbtTrieNodeGroup<TLayout>.Decode(blob);
        for (int slot = 0; slot < TLayout.BoundarySlots; slot++)
        {
            int position = PbtLayout.TrieNodeGroupBoundarySlotPosition(slot);
            if (group.KindAt(position) == PbtTrieNodeGroup.NodeKind.Chain)
            {
                flattened.Add(key.ChildGroup(slot, TLayout.LevelsPerGroup), group[position].ChainData.ToArray());
            }
        }
    }

    public RefCountingMemory? GetTrieNode(in TrieNodeKey key, in ValueHash256 hash)
    {
        lock (_lock) return Track(RefCountingMemory.WrappingOrNull(_nodes.GetValueOrDefault(key)));
    }

    public void SetTrieNode(in TrieNodeKey key, in ValueHash256 hash, RefCountingMemory? node)
    {
        byte[]? value = node?.ToArrayAndRelease();
        lock (_lock)
        {
            NodeWrites++;
            if (value is null) _nodes.Remove(key);
            else _nodes[key] = value;
        }
    }

    public RefCountingMemory? GetLeafBlob(in Stem stem, in ValueHash256 hash)
    {
        lock (_lock)
        {
            LeafReads++;
            return Track(RefCountingMemory.WrappingOrNull(_blobs.GetValueOrDefault(stem)));
        }
    }

    public void SetLeafBlob(in Stem stem, in ValueHash256 hash, RefCountingMemory? blob)
    {
        byte[]? value = blob?.ToArrayAndRelease();
        lock (_lock)
        {
            if (value is null) _blobs.Remove(stem);
            else _blobs[stem] = value;
        }
    }

    /// <remarks>Called under <see cref="_lock"/>, as everything it keeps is read from every worker.</remarks>
    private RefCountingMemory? Track(RefCountingMemory? memory)
    {
        _readThreads.Add(Environment.CurrentManagedThreadId);
        if (memory is not null) _handedOut.Add(memory);
        return memory;
    }

    /// <summary>Applies key/value writes (empty/zero value = clear) and returns the new root.</summary>
    public ValueHash256 ApplyBatch(IEnumerable<(byte[] Key, byte[]? Value)> writes)
    {
        using PbtWriteBatchBuilder builder = new();
        foreach ((byte[] key, byte[]? value) in writes)
        {
            ValueHash256 leaf = default;
            value?.CopyTo(leaf.BytesAsSpan);
            builder.SetLeaf(new Stem(key.AsSpan(0, Stem.Length)), key[Stem.Length], leaf);
        }

        using PbtWriteBatchSet batches = builder.DrainToWriteBatches(WriteLayout.Tiling());
        _roots = TrieUpdater.UpdateRoot(this, _roots, batches, memoryProvider, WriteLayout, RootFoldConcurrency, out _);
        return _roots.Root;
    }

    /// <summary>
    /// <inheritdoc cref="ApplyBatch" path="/summary"/>
    /// </summary>
    /// <remarks>
    /// Routes the writes through the production <see cref="PbtWriteBatchBuilder"/>, whose drain groups
    /// them by stem first byte and hands the updater the resulting bucket bounds — the path
    /// <see cref="ApplyBatch"/>, which builds its batch unordered, never takes.
    /// </remarks>
    public ValueHash256 ApplyDrainedBatch(IEnumerable<(byte[] Key, byte[]? Value)> writes)
    {
        using PbtWriteBatchBuilder builder = new();
        foreach ((byte[] key, byte[]? value) in writes)
        {
            ValueHash256 leaf = default;
            value?.CopyTo(leaf.BytesAsSpan);
            builder.SetLeaf(new Stem(key.AsSpan(0, Stem.Length)), key[Stem.Length], leaf);
        }

        using PbtWriteBatchSet batches = builder.DrainToWriteBatches(WriteLayout.Tiling());
        _roots = TrieUpdater.UpdateRoot(this, _roots, batches, memoryProvider, WriteLayout, RootFoldConcurrency, out _);
        return _roots.Root;
    }
}
