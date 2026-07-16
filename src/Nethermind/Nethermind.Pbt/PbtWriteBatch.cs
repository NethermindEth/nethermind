// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

/// <summary>
/// A batch of per-stem writes applied together by <see cref="TrieUpdater.UpdateRoot"/>, mirroring
/// the Patricia bulk-set interface: a plain list of one entry per stem, each carrying that stem's
/// sub-index → 32-byte value map. Grouping is the caller's job — every stem must be added exactly
/// once, so the caller merges all writes to a stem beforehand.
/// </summary>
/// <remarks>
/// A zero value clears the leaf. <see cref="Dispose"/> returns the pooled entry list and the per-stem
/// change maps. <see cref="Add"/> does not check for a duplicate stem — the descent detects one for
/// free, as a range that still holds several entries once it has consumed the whole stem.
/// </remarks>
public sealed class PbtWriteBatch(int estimatedStems) : IDisposable
{
    /// <param name="Stem">The 31-byte stem shared by every write in <paramref name="Changes"/>.</param>
    /// <param name="Changes">The stem's sub-index → 32-byte value writes; a zero value clears the leaf.</param>
    internal readonly record struct StemEntry(Stem Stem, IPbtStemChanges Changes);

    private readonly ArrayPoolList<StemEntry> _entries = new(estimatedStems);

    /// <summary>Adds <paramref name="stem"/>'s complete writes. The caller must merge duplicate stems itself; <see cref="TrieUpdater.UpdateRoot"/> throws on one.</summary>
    public void Add(in Stem stem, IPbtStemChanges changes) => _entries.Add(new StemEntry(stem, changes));

    /// <summary>The number of stems written; zero means the batch applies no changes.</summary>
    public int Count => _entries.Count;

    /// <remarks>Mutable: <see cref="TrieUpdater"/> permutes the entries in place as it partitions them by stem.</remarks>
    internal Span<StemEntry> Entries => _entries.AsSpan();

    public void Dispose()
    {
        foreach (StemEntry entry in _entries.AsSpan()) PbtStemChanges.Return(entry.Changes);
        _entries.Dispose();
    }
}
