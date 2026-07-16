// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

/// <summary>
/// A batch of per-stem writes applied together by <see cref="TrieUpdater.UpdateRoot"/>, mirroring
/// the Patricia bulk-set interface: a plain list of one entry per stem, each carrying that stem's
/// sub-index → 32-byte value map. Grouping is the caller's job — every stem must be added exactly
/// once (<see cref="Add"/> throws otherwise), so the caller merges all writes to a stem beforehand.
/// </summary>
/// <remarks>A zero value clears the leaf. <see cref="Dispose"/> returns the pooled entry list and the per-stem change maps.</remarks>
public sealed class PbtWriteBatch(int estimatedStems) : IDisposable
{
    /// <param name="Stem">The 31-byte stem shared by every write in <paramref name="Changes"/>.</param>
    /// <param name="Changes">The stem's sub-index → 32-byte value writes; a zero value clears the leaf.</param>
    internal readonly record struct StemEntry(Stem Stem, IPbtStemChanges Changes);

    private readonly ArrayPoolList<StemEntry> _entries = new(estimatedStems);
    private readonly HashSet<Stem> _stems = new(estimatedStems);

    /// <summary>Adds <paramref name="stem"/>'s complete writes. Throws if the stem was already added — the caller must merge duplicate stems itself.</summary>
    public void Add(in Stem stem, IPbtStemChanges changes)
    {
        if (!_stems.Add(stem)) throw new InvalidOperationException($"Stem {stem} added to the write batch more than once");
        _entries.Add(new StemEntry(stem, changes));
    }

    /// <summary>The number of stems written; zero means the batch applies no changes.</summary>
    public int Count => _entries.Count;

    internal ReadOnlySpan<StemEntry> Entries => _entries.AsSpan();

    public void Dispose()
    {
        foreach (StemEntry entry in _entries.AsSpan()) PbtStemChanges.Return(entry.Changes);
        _entries.Dispose();
        _stems.Clear();
    }
}
