// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

/// <summary>
/// A batch of tree-key → value writes applied together by <see cref="TrieUpdater.UpdateRoot"/>,
/// mirroring the Patricia bulk-set interface. It owns its data: each value is appended into one
/// contiguous pooled blob and the entry references the slice, so there is no per-value allocation.
/// </summary>
/// <remarks>Dispose returns the pooled buffers. Entries retain insertion order; the updater resolves duplicate keys last-write-wins.</remarks>
public sealed class PbtWriteBatch(int estimatedEntries, int estimatedBytes) : IDisposable
{
    /// <param name="Key">The 32-byte tree key: 31-byte stem followed by the 1-byte sub-index.</param>
    /// <param name="Offset">Start of the value within <see cref="PbtWriteBatch.Blob"/>.</param>
    /// <param name="Length">Value length; 0 clears the leaf.</param>
    public readonly record struct Entry(ValueHash256 Key, int Offset, int Length);

    private readonly ArrayPoolList<Entry> _entries = new(estimatedEntries);
    private readonly ArrayPoolList<byte> _blob = new(estimatedBytes);

    /// <summary>Appends a write. An empty <paramref name="value"/> clears the leaf.</summary>
    public void Add(in ValueHash256 key, ReadOnlySpan<byte> value)
    {
        _entries.Add(new Entry(key, _blob.Count, value.Length));
        _blob.AddRange(value);
    }

    public int Count => _entries.Count;

    internal ReadOnlySpan<Entry> Entries => _entries.AsSpan();

    internal ReadOnlySpan<byte> Blob => _blob.AsSpan();

    public void Dispose()
    {
        _entries.Dispose();
        _blob.Dispose();
    }
}
