// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Pbt;

/// <summary>Internal trie-group levels stored by an encoding; the final byte of every non-empty group.</summary>
/// <remarks>Formats represent the same trie and root while storing different levels; their values do not overlap chain encodings.</remarks>
public enum PbtGroupFormat : byte
{
    /// <summary>Every level of the tile.</summary>
    EveryLevel = 0x01,

    /// <summary>
    /// Every second internal level, anchored at the boundary — a kept node's stored children are its
    /// grandchildren. A skipped node's hash is folded from its children wherever it is needed, so
    /// nothing about the trie is lost. Stem nodes are stored wherever they land, skipped level or not.
    /// </summary>
    Interleaved = 0x03,

    /// <summary>
    /// The boundary alone: no internal node of the tile is stored, the whole of it being folded from
    /// the sixteen boundary entries on demand. Those cannot go the same way — a boundary entry is the
    /// link to the child group, stem or run below it, which no fold recovers without a lookup of its
    /// own.
    /// </summary>
    BoundaryOnly = 0x05,

    /// <summary>
    /// The boundary tile exactly as <see cref="BoundaryOnly"/>, paired with a stem blob that keeps one
    /// internal node every four depth rather than none. The tile is byte-for-byte a
    /// <see cref="BoundaryOnly"/> one bar its format byte; only the leaf column differs.
    /// </summary>
    Every4Depth = 0x07,

    /// <summary>
    /// Widths 64, 8 and 1, anchored at the boundary: every third depth and the mandatory boundary.
    /// </summary>
    /// <remarks>
    /// In its intended six-level tile, only width-8 intermediate hashes are stored because the parent
    /// caches the width-64 group root. 0x09 is distinct from the group and chain sentinels.
    /// </remarks>
    Every3Depth = 0x09,
}
