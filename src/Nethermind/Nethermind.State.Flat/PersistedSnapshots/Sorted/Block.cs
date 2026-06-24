// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Nethermind.State.Flat.PersistedSnapshots.Sorted;

/// <summary>
/// A single, self-describing, binary-searchable block of front-coded key/value records — the shared
/// unit of both the data blocks and the top-level index of a <see cref="SortedTable"/>.
/// </summary>
/// <remarks>
/// Wire layout (offsets relative to the block start):
/// <code>
///   [formatFlag u8]                     ; Block ⇒ W = 2, Index ⇒ W = 4 (offset width in bytes)
///   [recordsEnd  : W]                   ; block-relative byte offset where records end (content size)
///   [numRestarts : W]
///   [restartOffset : W × numRestarts]   ; block-relative; restartOffset[0] = 1 + 2W + W·numRestarts
///   [records...]                        ; [cp u8][suffixLen u8][keySuffix][vs u8][value]
/// </code>
/// Keys are front-coded against the previous record, resetting (<c>cp = 0</c>, full key) every
/// <c>restartInterval</c> records and at the block start — these are the <em>restarts</em>. The
/// header <c>formatFlag</c> records the block's role and thereby its offset width — a data
/// <c>Block</c> (capped well under 64 KiB) uses 2-byte offsets, the multi-MB <c>Index</c> uses
/// 4-byte — so one format serves both. <see cref="BlockReader.SeekCeiling"/> binary searches the
/// restarts then scans to <c>recordsEnd</c> for the first key ≥ the target (LevelDB
/// <c>Block::Iter::Seek</c>).
/// </remarks>
internal static class Block
{
    /// <summary>Width of the single-byte record fields (common-prefix, key-suffix size, value size).</summary>
    internal const int SizePrefix = sizeof(byte);

    // On-disk header flag selecting the block's role and thereby its offset width. A data Block is
    // capped at BlockSize (well under 64 KiB) so it uses 2-byte offsets; the Index can be multi-MB
    // and uses 4-byte offsets — one format serves both.
    internal const byte FlagBlock = 1;   // 2-byte offsets
    internal const byte FlagIndex = 2;   // 4-byte offsets

    /// <summary>Offset width in bytes for <paramref name="flag"/>, or 0 if it is neither
    /// <see cref="FlagBlock"/> nor <see cref="FlagIndex"/>.</summary>
    internal static int WidthFromFlag(byte flag) => flag switch
    {
        FlagBlock => 2,
        FlagIndex => 4,
        _ => 0,
    };

    /// <summary>Block-relative byte offset of the first record, given the offset width and restart count.</summary>
    internal static long RecordsStart(int width, long numRestarts) => 1 + 2L * width + (long)width * numRestarts;

    internal static long ReadOffset(scoped ReadOnlySpan<byte> src, int width) =>
        width == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(src) : BinaryPrimitives.ReadUInt32LittleEndian(src);

    /// <summary>The fixed 2-byte prefix every record starts with: the key's common-prefix length with
    /// the previous record, then the length of the key suffix that follows. Read by reinterpreting the
    /// two header bytes (both single bytes, so endianness-independent).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal readonly struct RecordHeader
    {
        internal readonly byte CommonPrefix;
        internal readonly byte SuffixLength;
    }
}
