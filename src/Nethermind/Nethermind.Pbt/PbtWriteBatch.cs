// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

/// <summary>
/// A batch of tree-key → value writes applied together by <see cref="TrieUpdater.UpdateRoot"/>,
/// mirroring the Patricia bulk-set interface. Every EIP-8297 leaf value is exactly 32 bytes, so the
/// value is stored inline in the entry (a zero value clears the leaf); the entries live in one
/// pooled list that <see cref="Dispose"/> returns.
/// </summary>
/// <remarks>Entries retain insertion order; the updater resolves duplicate keys last-write-wins.</remarks>
public sealed class PbtWriteBatch(int estimatedEntries) : IDisposable
{
    /// <param name="Key">The 32-byte tree key: 31-byte stem followed by the 1-byte sub-index.</param>
    /// <param name="Value">The 32-byte leaf value; a zero value clears the leaf.</param>
    public readonly record struct Entry(ValueHash256 Key, ValueHash256 Value);

    private readonly ArrayPoolList<Entry> _entries = new(estimatedEntries);

    /// <summary>Appends a write. An empty <paramref name="value"/> (or all zero) clears the leaf.</summary>
    public void Add(in ValueHash256 key, ReadOnlySpan<byte> value)
    {
        ValueHash256 leaf = default;
        value.CopyTo(leaf.BytesAsSpan);
        _entries.Add(new Entry(key, leaf));
    }

    public int Count => _entries.Count;

    internal ReadOnlySpan<Entry> Entries => _entries.AsSpan();

    public void Dispose() => _entries.Dispose();
}
