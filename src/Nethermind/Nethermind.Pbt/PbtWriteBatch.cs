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
/// A zero value clears the leaf. <see cref="Dispose"/> returns the pooled entry list, the per-stem
/// change maps and <paramref name="buckets"/>. <see cref="Add"/> does not check for a duplicate stem —
/// the descent detects one for free, as a range that still holds several entries once it has consumed
/// the whole stem.
/// </remarks>
/// <param name="buckets">
/// The precalculated bucket table for entries added in ascending stem-first-byte order, whose lease
/// passes to this batch; <c>null</c> when the producer adds its entries in no particular order, which
/// leaves <see cref="TrieUpdater"/> to partition them itself.
/// </param>
public sealed class PbtWriteBatch(int estimatedStems, ArrayPoolList<int>? buckets) : IDisposable
{
    /// <summary>One level of the bucket table: a bounds array, so bucket <c>i</c> is <c>entries[level[i]..level[i + 1]]</c>.</summary>
    public const int LevelStride = PbtTrieNodeGroup.BoundarySlots + 1;

    /// <summary>
    /// The byte level of the bucket table: one bounds array per nibble group, group <c>h</c> covering the
    /// stem first bytes <c>0xh0</c>..<c>0xhF</c>. The nibble level's own bounds array follows it.
    /// </summary>
    /// <remarks>
    /// A group's ends count from the start of its nibble rather than of the batch, which is what lets the
    /// depth-4 descent use them as its bounds unchanged; the nibble level, whose range is the whole batch,
    /// is the same thing at depth 0. The coarse level sits last so a descent finds its own level at the
    /// table's end whatever the level count, and slot <c>h</c>'s child level at <c>h * LevelStride</c>.
    /// </remarks>
    public const int ByteLevelLength = PbtTrieNodeGroup.BoundarySlots * LevelStride;

    /// <summary>The whole bucket table: the byte level, then the nibble level.</summary>
    public const int BucketTableLength = ByteLevelLength + LevelStride;

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

    /// <summary>The precalculated depth-0 and depth-4 bucket bounds, or empty when the entries are in no particular order.</summary>
    internal ReadOnlySpan<int> Buckets => buckets is null ? default : buckets.AsSpan();

    public void Dispose()
    {
        foreach (StemEntry entry in _entries.AsSpan()) PbtStemChanges.Return(entry.Changes);
        _entries.Dispose();
        buckets?.Dispose();
    }
}
