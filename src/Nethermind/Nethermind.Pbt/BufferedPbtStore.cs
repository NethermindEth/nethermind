// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;

namespace Nethermind.Pbt;

/// <summary>
/// An <see cref="IPbtStore"/> that reads straight through to <paramref name="inner"/> but holds its writes
/// back, replaying them into it in order on <see cref="Flush"/>.
/// </summary>
/// <remarks>
/// This is what lets <see cref="TrieUpdater"/> descend several subtrees at once over a store that takes
/// neither concurrent writes nor a read racing one: each subtree writes into a buffer of its own, and the
/// frame that spawned them replays those buffers only once every one of them is back, so the store behind
/// them is still only ever touched by one thread at a time.
/// <para>
/// Reads pass straight through because <see cref="TrieUpdater.UpdateRoot"/> reads each node and blob at most
/// once and before writing it, so no read of the descent's can be looking for a write still held here.
/// </para>
/// <para>
/// A write hands over its lease (see <see cref="IPbtStore"/>), so this owns what it holds: <see cref="Flush"/>
/// passes each lease on to <paramref name="inner"/>, and <see cref="Dispose"/> releases whatever is left, an
/// abandoned descent's writes having no other owner to free their pooled memory.
/// </para>
/// </remarks>
internal sealed class BufferedPbtStore(IPbtStore inner) : IPbtStore, IDisposable
{
    /// <summary>Enough for the small subtrees; a large one grows the list a handful of times.</summary>
    private const int InitialCapacity = 64;

    private readonly ArrayPoolList<(TrieNodeKey Key, RefCountingMemory? Node)> _nodes = new(InitialCapacity);
    private readonly ArrayPoolList<(Stem Stem, RefCountingMemory? Blob)> _blobs = new(InitialCapacity);

    public RefCountingMemory? GetTrieNode(in TrieNodeKey key) => inner.GetTrieNode(key);

    public RefCountingMemory? GetLeafBlob(in Stem stem) => inner.GetLeafBlob(stem);

    public void SetTrieNode(in TrieNodeKey key, RefCountingMemory? node) => _nodes.Add((key, node));

    public void SetLeafBlob(in Stem stem, RefCountingMemory? blob) => _blobs.Add((stem, blob));

    /// <summary>Replays the buffered writes into the inner store, handing each lease on with them.</summary>
    /// <remarks>In the order they were made, so a key written twice ends up holding what it did before.</remarks>
    public void Flush()
    {
        foreach ((TrieNodeKey key, RefCountingMemory? node) in _nodes.AsSpan()) inner.SetTrieNode(key, node);
        _nodes.Clear();

        foreach ((Stem stem, RefCountingMemory? blob) in _blobs.AsSpan()) inner.SetLeafBlob(stem, blob);
        _blobs.Clear();
    }

    public void Dispose()
    {
        foreach ((_, RefCountingMemory? node) in _nodes.AsSpan()) ((IDisposable?)node)?.Dispose();
        foreach ((_, RefCountingMemory? blob) in _blobs.AsSpan()) ((IDisposable?)blob)?.Dispose();

        _nodes.Dispose();
        _blobs.Dispose();
    }
}
