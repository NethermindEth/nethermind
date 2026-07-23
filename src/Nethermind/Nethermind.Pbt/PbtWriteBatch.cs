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
/// <see cref="Dispose"/> returns the pooled entry list, the per-stem change maps and
/// <paramref name="buckets"/>. <see cref="Add"/> does not check for a duplicate stem — the descent
/// detects and throws on one for free, as a range that still holds several entries once it has
/// consumed the whole stem.
/// </remarks>
/// <param name="buckets">
/// The precalculated bucket table for entries added in ascending stem-first-byte order, whose lease
/// passes to this batch; <c>null</c> when the producer adds its entries in no particular order, which
/// leaves <see cref="TrieUpdater"/> to partition them itself.
/// </param>
public sealed class PbtWriteBatch(int estimatedStems, ArrayPoolList<int>? buckets) : IDisposable
{
    /// <summary>The bounds array of one level, so bucket <c>i</c> is <c>entries[level[i]..level[i + 1]]</c>.</summary>
    public static int BoundsLength<TLayout>() where TLayout : IPbtTileLayout => TLayout.BoundarySlots + 1;

    /// <summary>
    /// Where a level caches its touched mask: bit <c>i</c> set where bucket <c>i</c> is non-empty, which is
    /// what the descent partitions on. Derived from the counts the bucketing already walks, so that a
    /// consumer never re-derives it by scanning the bounds.
    /// </summary>
    /// <remarks>
    /// Two entries, low half first: a wide tiling's mask does not fit the <c>int</c> the bounds — which
    /// are entry indices — make the level out of.
    /// </remarks>
    public static int TouchedMaskIndex<TLayout>() where TLayout : IPbtTileLayout => BoundsLength<TLayout>();

    /// <summary>One level of the bucket table: its <see cref="BoundsLength{TLayout}"/> bounds, then its <see cref="TouchedMaskIndex{TLayout}"/>.</summary>
    public static int LevelStride<TLayout>() where TLayout : IPbtTileLayout => BoundsLength<TLayout>() + 2;

    public static ulong ReadTouched<TLayout>(scoped ReadOnlySpan<int> level) where TLayout : IPbtTileLayout
    {
        int index = TouchedMaskIndex<TLayout>();
        return (uint)level[index] | ((ulong)(uint)level[index + 1] << 32);
    }

    public static void WriteTouched<TLayout>(Span<int> level, ulong touched) where TLayout : IPbtTileLayout
    {
        int index = TouchedMaskIndex<TLayout>();
        level[index] = (int)(uint)touched;
        level[index + 1] = (int)(uint)(touched >> 32);
    }

    /// <param name="Stem">The 31-byte stem shared by every write in <paramref name="Changes"/>.</param>
    /// <param name="Changes">The stem's sub-index → 32-byte value writes; a zero value clears the leaf.</param>
    internal readonly record struct StemEntry(Stem Stem, IPbtStemChanges Changes);

    private readonly ArrayPoolList<StemEntry> _entries = new(estimatedStems);

    public void Add(in Stem stem, IPbtStemChanges changes) => _entries.Add(new StemEntry(stem, changes));

    public int Count => _entries.Count;

    /// <remarks>Mutable: <see cref="TrieUpdater"/> permutes the entries in place as it partitions them by stem.</remarks>
    internal Span<StemEntry> Entries => _entries.AsSpan();

    internal ReadOnlySpan<int> Buckets => buckets is null ? default : buckets.AsSpan();

    /// <summary>
    /// The array <see cref="Entries"/> spans, so that a range of it can be named by index rather than
    /// by a span: what lets <see cref="TrieUpdater"/> hand one bucket to another thread, a span being
    /// unable to leave the stack.
    /// </summary>
    internal StemEntry[] EntriesArray => _entries.UnsafeGetInternalArray();

    /// <inheritdoc cref="EntriesArray" path="/summary"/>
    internal int[]? BucketsArray => buckets?.UnsafeGetInternalArray();

    public void Dispose()
    {
        foreach (StemEntry entry in _entries.AsSpan()) PbtStemChanges.Return(entry.Changes);
        _entries.Dispose();
        buckets?.Dispose();
    }
}
