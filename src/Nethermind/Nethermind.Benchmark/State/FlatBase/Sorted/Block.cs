// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// Verbatim copy of Nethermind.State.Flat/PersistedSnapshots/Sorted/Block.cs (internal in
// Nethermind.State.Flat, which has no InternalsVisibleTo for benchmarks). Benchmark-scoped
// prototype code — keep in sync with the original; do not diverge.

using System.Runtime.InteropServices;

namespace Nethermind.Benchmarks.State.FlatBase.Sorted;

/// <summary>
/// A single, self-describing, binary-searchable block of key/value records with front-coded keys — the shared
/// unit of both the data blocks and the top-level index of a <see cref="SortedTable"/>.
/// </summary>
internal static class Block
{
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

    /// <summary>Fixed 3-byte prefix of a data record: the key's common-prefix length with the previous
    /// record, the length of the key suffix that follows, and the inline value's length. Layout
    /// <c>[cp][suffixLen][valueLen][keySuffix][value]</c>, so the prefix is read in one blit and the key
    /// then value are sliced from the bytes after it. Single-byte fields, so endianness-independent.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal readonly record struct DataRecordHeader(byte CommonPrefix, byte SuffixLength, byte ValueLength);

    /// <summary>Fixed 3-byte prefix of an index record: the front-coded key (cp, suffixLen) then the
    /// number of little-endian low-order value bytes stored in <see cref="ValueChangedLength"/>. Layout
    /// <c>[cp][suffixLen][valChangedLen][keySuffix][valChanged]</c>; the value (a data-block byte offset)
    /// keeps the high bytes of the previous record's value and overwrites only its low bytes, reset against
    /// 0 at a <c>cp == 0</c> restart (see <see cref="BlockBuilder.AddChangedPrefixValue"/>).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal readonly record struct IndexRecordHeader(byte CommonPrefix, byte SuffixLength, byte ValueChangedLength);
}
