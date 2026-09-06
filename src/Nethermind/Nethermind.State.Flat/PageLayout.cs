// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat;

/// <summary>
/// Page-alignment constants shared by the flat-state on-disk writers. The 4 KiB page size
/// matches the typical OS page granularity targeted by the mmap-backed arenas; writers
/// pad to this size so a single value (trie-node RLP in a blob arena, sorted-table block)
/// never straddles a page that the reader would have to fault in just to splice across
/// the seam.
/// </summary>
public static class PageLayout
{
    /// <summary>Logical page size for blob-arena and sorted-table index alignment.</summary>
    public const int PageSize = 4096;

    /// <summary>
    /// Bitmask companion to <see cref="PageSize"/> for computing in-page offsets:
    /// <c>offsetInPage = absoluteOffset &amp; PageMask</c>. Typed as <see cref="long"/>
    /// because callers mask file-absolute offsets that may exceed 31 bits.
    /// </summary>
    public const long PageMask = PageSize - 1;

    /// <summary>
    /// Bytes-to-next-page threshold below which the sorted-table builder pads up to the next
    /// page boundary before writing the next node. The page-crossing heuristic stops a
    /// node growing into the next page; padding eats the small leftover so the next
    /// node opens on a fresh page. Threshold is intentionally large so most splits earn
    /// the alignment; nodes finalised well inside their page (gap &gt; threshold) skip
    /// padding to avoid writing kilobytes of zeros.
    /// </summary>
    public const int PadThreshold = 64;

    /// <summary>
    /// OS memory-page size — the granularity of <c>madvise</c> / <c>posix_fadvise</c> /
    /// <c>fallocate(PUNCH_HOLE)</c>. Distinct from <see cref="PageSize"/>, the fixed 4 KiB
    /// logical page used for on-disk node alignment.
    /// </summary>
    public static readonly int OsPageSize = Environment.SystemPageSize;

    public static long RoundUpToOsPage(long value) => (value + OsPageSize - 1) & ~((long)OsPageSize - 1);
}
