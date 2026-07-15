// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

/// <summary>
/// A batch of tree-key → value writes applied together by <see cref="TrieUpdater.UpdateRoot"/>,
/// mirroring the Patricia bulk-set interface. Writes are grouped by their 31-byte stem as they are
/// added — each stem carries a sub-index → 32-byte value map — because the producer naturally emits
/// consecutive writes sharing a stem, and the per-stem map is exactly what a stem's leaf blob folds.
/// </summary>
/// <remarks>Duplicate writes resolve last-write-wins per (stem, sub-index); a zero value clears the leaf.</remarks>
public sealed class PbtWriteBatch(int estimatedStems) : IDisposable
{
    /// <param name="Stem">The 31-byte stem shared by every write in <paramref name="Changes"/>.</param>
    /// <param name="Changes">The stem's sub-index → 32-byte value writes; a zero value clears the leaf.</param>
    internal readonly record struct StemEntry(Stem Stem, Dictionary<byte, ValueHash256> Changes);

    private readonly Dictionary<Stem, Dictionary<byte, ValueHash256>> _byStem = new(estimatedStems);

    /// <summary>Appends a write to <paramref name="stem"/>'s sub-index <paramref name="subIndex"/>. An empty <paramref name="value"/> (or all zero) clears the leaf.</summary>
    public void Add(in Stem stem, byte subIndex, ReadOnlySpan<byte> value)
    {
        if (!_byStem.TryGetValue(stem, out Dictionary<byte, ValueHash256>? changes))
        {
            _byStem[stem] = changes = [];
        }

        ValueHash256 leaf = default;
        value.CopyTo(leaf.BytesAsSpan);
        changes[subIndex] = leaf;
    }

    /// <summary>The number of distinct stems written; zero means the batch applies no changes.</summary>
    public int Count => _byStem.Count;

    internal Dictionary<Stem, Dictionary<byte, ValueHash256>> Stems => _byStem;

    public void Dispose() => _byStem.Clear();
}
