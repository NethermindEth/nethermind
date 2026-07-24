// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Pbt;

/// <summary>Which levels of a <see cref="PbtTrieNodeGroup"/> an encoding stores internal nodes for; the last byte of every non-empty encoding.</summary>
/// <remarks>
/// All of them describe the same trie and fold to the same root — they differ only in how much of
/// the fold they write down — so a store may hold any of them at any key, and a group converts only
/// when a change rewrites it. The values are disjoint from <see cref="PbtNodeChain"/>'s and from
/// <see cref="PbtNodeCluster"/>'s, so an encoding of any of the three says which it is.
/// </remarks>
public enum PbtGroupFormat : byte
{
    /// <summary>Every level of the tile, the original encoding.</summary>
    EveryLevel = 0x01,

    /// <summary>
    /// Only the even group-relative levels (0, 2 and the boundary), skipping the internal nodes of
    /// levels 1 and 3 — a kept node's stored children are its grandchildren. A skipped node's hash is
    /// folded from its children wherever it is needed, so nothing about the trie is lost. Stem nodes
    /// are stored wherever they land, skipped level or not.
    /// </summary>
    Interleaved = 0x03,

    /// <summary>
    /// The boundary alone: no internal node of the tile is stored, the whole of it being folded from
    /// the sixteen boundary entries on demand. Those cannot go the same way — a boundary entry is the
    /// link to the child group, stem or run below it, which no fold recovers without a lookup of its
    /// own.
    /// </summary>
    /// <remarks>0x04 is <see cref="PbtNodeCluster"/>'s, whose encoding a group's has to be told from.</remarks>
    BoundaryOnly = 0x05,

    /// <summary>
    /// The boundary tile exactly as <see cref="BoundaryOnly"/>, paired with a stem blob that keeps one
    /// internal node every four depth rather than none. The tile is byte-for-byte a
    /// <see cref="BoundaryOnly"/> one bar its format byte; only the leaf column differs.
    /// </summary>
    Every4Depth = 0x07,
}
