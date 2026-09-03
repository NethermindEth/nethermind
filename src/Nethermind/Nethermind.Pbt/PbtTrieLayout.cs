// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Pbt;

/// <summary>
/// The layout a store is written in: the tiling of the stem trie its keys follow, and which levels of
/// a tile and of a stem's leaf blob hold a stored node.
/// </summary>
/// <remarks>
/// The two are named together because a store only ever has one of them as a whole, and only some of
/// their combinations are worth running — so the names repeat a tiling or a set of levels between
/// them. They are not alike in what a change to one costs: the tiling half fixes the keys, so a store
/// holds one and never both, while the levels half only decides how much of the fold is written down
/// and may change under a store that already holds the other.
/// </remarks>
public enum PbtTrieLayout : byte
{
    /// <inheritdoc cref="PbtTiling.SixLevel"/>
    /// <remarks>Every other level stored, on both sides (<see cref="PbtGroupFormat.Interleaved"/>).</remarks>
    SixLevelInterleaved = 3,

    /// <inheritdoc cref="PbtTiling.EightLevel"/>
    /// <remarks>Every other level stored, on both sides (<see cref="PbtGroupFormat.Interleaved"/>).</remarks>
    EightLevelInterleaved = 4,

    /// <inheritdoc cref="PbtTiling.EightLevel"/>
    /// <remarks>
    /// No internal node stored in a tile, and one every four depth in a leaf blob
    /// (<see cref="PbtGroupFormat.Every4Depth"/>).
    /// </remarks>
    EightLevelEvery4Depth = 5,

    /// <inheritdoc cref="PbtTiling.FourLevel"/>
    /// <remarks>Every other level stored, on both sides (<see cref="PbtGroupFormat.Interleaved"/>).</remarks>
    FourLevelInterleaved = 6,

    /// <inheritdoc cref="PbtTiling.FourLevel"/>
    /// <remarks>No internal node stored, on either side (<see cref="PbtGroupFormat.BoundaryOnly"/>).</remarks>
    FourLevelBoundaryOnly = 7,

    /// <inheritdoc cref="PbtTiling.SixLevel"/>
    /// <remarks>
    /// Trie groups retain every third depth (<see cref="PbtGroupFormat.Every3Depth"/>), while stem leaf
    /// blobs retain the existing every-other-depth <see cref="PbtLeafFormat.Interleaved"/> policy.
    /// </remarks>
    SixLevelEvery3Depth = 8,

    /// <inheritdoc cref="PbtTiling.FiveLevel"/>
    /// <remarks>Every other level stored, on both sides (<see cref="PbtGroupFormat.Interleaved"/>).</remarks>
    FiveLevelInterleaved = 9,

    /// <inheritdoc cref="PbtTiling.FourLevel"/>
    /// <remarks>Every level of a tile and of a leaf blob stored.</remarks>
    FourLevelEveryLevel = 10,
}

public static class PbtTrieLayoutExtensions
{
    /// <summary>The tiling <paramref name="layout"/> keys its trie nodes by.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="layout"/> is no <see cref="PbtTrieLayout"/>.</exception>
    public static PbtTiling Tiling(this PbtTrieLayout layout) => layout switch
    {
        PbtTrieLayout.SixLevelInterleaved or PbtTrieLayout.SixLevelEvery3Depth => PbtTiling.SixLevel,
        PbtTrieLayout.EightLevelInterleaved or PbtTrieLayout.EightLevelEvery4Depth => PbtTiling.EightLevel,
        PbtTrieLayout.FourLevelEveryLevel or PbtTrieLayout.FourLevelInterleaved or PbtTrieLayout.FourLevelBoundaryOnly => PbtTiling.FourLevel,
        PbtTrieLayout.FiveLevelInterleaved => PbtTiling.FiveLevel,
        _ => throw new ArgumentOutOfRangeException(nameof(layout)),
    };

    /// <summary>The encoding <paramref name="layout"/> writes a rebuilt group, and with it a leaf blob, in.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="layout"/> is no <see cref="PbtTrieLayout"/>.</exception>
    public static PbtGroupFormat GroupFormat(this PbtTrieLayout layout) => layout switch
    {
        PbtTrieLayout.FourLevelEveryLevel => PbtGroupFormat.EveryLevel,
        PbtTrieLayout.SixLevelInterleaved
            or PbtTrieLayout.EightLevelInterleaved
            or PbtTrieLayout.FourLevelInterleaved
            or PbtTrieLayout.FiveLevelInterleaved => PbtGroupFormat.Interleaved,
        PbtTrieLayout.FourLevelBoundaryOnly => PbtGroupFormat.BoundaryOnly,
        PbtTrieLayout.EightLevelEvery4Depth => PbtGroupFormat.Every4Depth,
        PbtTrieLayout.SixLevelEvery3Depth => PbtGroupFormat.Every3Depth,
        _ => throw new ArgumentOutOfRangeException(nameof(layout)),
    };
}
