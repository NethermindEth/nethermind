// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
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

    /// <summary>The number of 32-bit words used to cache one level's touched slots.</summary>
    /// <remarks>The existing layouts retain their two-word representation; wider layouts add words as needed.</remarks>
    public static int TouchedWordCount<TLayout>() where TLayout : IPbtTileLayout =>
        Math.Max(2, (TLayout.BoundarySlots + 31) / 32);

    /// <summary>One level of the bucket table: its bounds followed by its touched-mask words.</summary>
    public static int LevelStride<TLayout>() where TLayout : IPbtTileLayout => BoundsLength<TLayout>() + TouchedWordCount<TLayout>();

    /// <summary>The little-endian 32-bit words caching which buckets in <paramref name="level"/> are non-empty.</summary>
    public static ReadOnlySpan<int> ReadTouched<TLayout>(ReadOnlySpan<int> level) where TLayout : IPbtTileLayout =>
        level.Slice(TouchedMaskIndex<TLayout>(), TouchedWordCount<TLayout>());

    public static bool ContainsTouched<TLayout>(scoped ReadOnlySpan<int> level, int slot) where TLayout : IPbtTileLayout =>
        ((uint)ReadTouched<TLayout>(level)[slot >> 5] & (1u << (slot & 31))) != 0;

    public static bool HasMultipleTouched<TLayout>(scoped ReadOnlySpan<int> level) where TLayout : IPbtTileLayout
    {
        bool found = false;
        foreach (int word in ReadTouched<TLayout>(level))
        {
            uint bits = (uint)word;
            if (bits == 0) continue;
            if (found || !BitOperations.IsPow2(bits)) return true;
            found = true;
        }

        return false;
    }

    public static void ClearTouched<TLayout>(Span<int> level) where TLayout : IPbtTileLayout =>
        level.Slice(TouchedMaskIndex<TLayout>(), TouchedWordCount<TLayout>()).Clear();

    public static void SetTouched<TLayout>(Span<int> level, int slot) where TLayout : IPbtTileLayout
    {
        int word = TouchedMaskIndex<TLayout>() + (slot >> 5);
        level[word] = (int)((uint)level[word] | (1u << (slot & 31)));
    }

    /// <summary>Writes the touched slots of a layout whose boundary fits in 64 bits.</summary>
    public static void WriteTouched<TLayout>(Span<int> level, ulong touched) where TLayout : IPbtTileLayout
    {
        int index = TouchedMaskIndex<TLayout>();
        ClearTouched<TLayout>(level);
        level[index] = (int)(uint)touched;
        level[index + 1] = (int)(uint)(touched >> 32);
    }

    /// <summary>Reads the touched slots of a layout whose boundary fits in 64 bits back as a bitmask.</summary>
    /// <remarks>The inverse of <see cref="WriteTouched"/>; the low two mask words are always present, since <see cref="TouchedWordCount"/> is at least two.</remarks>
    public static ulong ReadTouchedBitmask<TLayout>(scoped ReadOnlySpan<int> level) where TLayout : IPbtTileLayout
    {
        ReadOnlySpan<int> touched = ReadTouched<TLayout>(level);
        return (uint)touched[0] | ((ulong)(uint)touched[1] << 32);
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
