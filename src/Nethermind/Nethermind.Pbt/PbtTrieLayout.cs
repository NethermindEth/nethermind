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
    /// <inheritdoc cref="PbtTiling.ClusteredFourLevel"/>
    /// <remarks>Every level of a tile and of a leaf blob stored, the original layout.</remarks>
    ClusteredFourLevelEveryLevel,

    /// <inheritdoc cref="PbtTiling.ClusteredFourLevel"/>
    /// <remarks>Every other level stored, on both sides (<see cref="PbtGroupFormat.Interleaved"/>).</remarks>
    ClusteredFourLevelInterleaved,

    /// <inheritdoc cref="PbtTiling.ClusteredFourLevel"/>
    /// <remarks>No internal node stored, on either side (<see cref="PbtGroupFormat.BoundaryOnly"/>).</remarks>
    ClusteredFourLevelBoundaryOnly,

    /// <inheritdoc cref="PbtTiling.SixLevel"/>
    /// <remarks>Every other level stored, on both sides (<see cref="PbtGroupFormat.Interleaved"/>).</remarks>
    SixLevelInterleaved,

    /// <inheritdoc cref="PbtTiling.EightLevel"/>
    /// <remarks>Every other level stored, on both sides (<see cref="PbtGroupFormat.Interleaved"/>).</remarks>
    EightLevelInterleaved,

    /// <inheritdoc cref="PbtTiling.EightLevel"/>
    /// <remarks>
    /// No internal node stored in a tile, and one every four depth in a leaf blob
    /// (<see cref="PbtGroupFormat.Every4Depth"/>).
    /// </remarks>
    EightLevelEvery4Depth,
}

public static class PbtTrieLayoutExtensions
{
    /// <summary>The tiling <paramref name="layout"/> keys its trie nodes by.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="layout"/> is no <see cref="PbtTrieLayout"/>.</exception>
    public static PbtTiling Tiling(this PbtTrieLayout layout) => layout switch
    {
        PbtTrieLayout.ClusteredFourLevelEveryLevel
            or PbtTrieLayout.ClusteredFourLevelInterleaved
            or PbtTrieLayout.ClusteredFourLevelBoundaryOnly => PbtTiling.ClusteredFourLevel,
        PbtTrieLayout.SixLevelInterleaved => PbtTiling.SixLevel,
        PbtTrieLayout.EightLevelInterleaved or PbtTrieLayout.EightLevelEvery4Depth => PbtTiling.EightLevel,
        _ => throw new ArgumentOutOfRangeException(nameof(layout)),
    };

    /// <summary>The encoding <paramref name="layout"/> writes a rebuilt group, and with it a leaf blob, in.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="layout"/> is no <see cref="PbtTrieLayout"/>.</exception>
    public static PbtGroupFormat GroupFormat(this PbtTrieLayout layout) => layout switch
    {
        PbtTrieLayout.ClusteredFourLevelEveryLevel => PbtGroupFormat.EveryLevel,
        PbtTrieLayout.ClusteredFourLevelInterleaved
            or PbtTrieLayout.SixLevelInterleaved
            or PbtTrieLayout.EightLevelInterleaved => PbtGroupFormat.Interleaved,
        PbtTrieLayout.ClusteredFourLevelBoundaryOnly => PbtGroupFormat.BoundaryOnly,
        PbtTrieLayout.EightLevelEvery4Depth => PbtGroupFormat.Every4Depth,
        _ => throw new ArgumentOutOfRangeException(nameof(layout)),
    };
}
