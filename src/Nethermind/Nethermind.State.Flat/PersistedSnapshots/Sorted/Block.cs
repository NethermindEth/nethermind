// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;

namespace Nethermind.State.Flat.PersistedSnapshots.Sorted;

/// <summary>
/// A single, self-describing, binary-searchable block of key/value records with front-coded keys — the shared
/// unit of both the data blocks and the top-level index of a <see cref="SortedTable"/>.
/// </summary>
/// <remarks>
/// Wire layout (offsets relative to the block start):
/// <code>
///   [formatFlag u8]                     ; Block ⇒ W = 2, Index ⇒ W = 4 (offset width in bytes)
///   [recordsEnd  : W]                   ; block-relative byte offset where records end (content size)
///   [numRestarts : W]
///   [restartOffset : W × numRestarts]   ; block-relative; restartOffset[0] = 1 + 2W + W·numRestarts
///   data record                         ; [cp u8][suffixLen u8][valueLen u8][keySuffix][value]
///   index record                        ; [cp u8][suffixLen u8][valChangedLen u8][keySuffix][valChanged]
/// </code>
/// Each record opens with a fixed prefix carrying all the lengths, so a reader blits the prefix then
/// slices the key (and value) after it. Keys are front-coded against the previous record. A record with
/// <c>cp == 0</c> (it stores a full key) is a <em>restart</em>: the builder forces one at least every
/// <c>restartInterval</c> records (a build-time knob, not stored on disk) to bound scan length, and one
/// also arises wherever adjacent keys share no leading byte. The restart table indexes every restart. The
/// index block's value (a data-block byte offset) keeps the high (little-endian) bytes of the previous
/// value and stores only the low bytes that changed — reset against 0 at each restart (see
/// <see cref="BlockBuilder.AddChangedPrefixValue"/>). The
/// header <c>formatFlag</c> records the block's role and thereby its offset width — a data
/// <c>Block</c> (capped well under 64 KiB) uses 2-byte offsets, the multi-MB <c>Index</c> uses
/// 4-byte — so one format serves both. <see cref="DataBlockReader.SeekCeiling"/> binary searches the
/// restarts then scans to <c>recordsEnd</c> for the first key ≥ the target (LevelDB
/// <c>Block::Iter::Seek</c>).
/// </remarks>
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
    internal readonly struct DataRecordHeader(byte commonPrefix, byte suffixLength, byte valueLength)
    {
        internal readonly byte CommonPrefix = commonPrefix;
        internal readonly byte SuffixLength = suffixLength;
        internal readonly byte ValueLength = valueLength;
    }

    /// <summary>Fixed 3-byte prefix of an index record: the front-coded key (cp, suffixLen) then the
    /// number of little-endian low-order value bytes stored in <see cref="ValueChangedLength"/>. Layout
    /// <c>[cp][suffixLen][valChangedLen][keySuffix][valChanged]</c>; the value (a data-block byte offset)
    /// keeps the high bytes of the previous record's value and overwrites only its low bytes, reset against
    /// 0 at a <c>cp == 0</c> restart (see <see cref="BlockBuilder.AddChangedPrefixValue"/>).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal readonly struct IndexRecordHeader(byte commonPrefix, byte suffixLength, byte valueChangedLength)
    {
        internal readonly byte CommonPrefix = commonPrefix;
        internal readonly byte SuffixLength = suffixLength;
        internal readonly byte ValueChangedLength = valueChangedLength;
    }
}
