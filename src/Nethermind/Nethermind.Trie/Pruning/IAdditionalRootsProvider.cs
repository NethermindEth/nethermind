// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Trie.Pruning;

/// <summary>
/// Allows external components to hook into the full pruning and shutdown persistence pipeline.
/// </summary>
public interface IAdditionalRootsProvider
{
    /// <summary>
    /// Copies trie nodes to <paramref name="target"/>, reading from any available source (in-memory overlay, disk).
    /// <para>
    /// When <paramref name="upToBlockNumberExclusive"/> is provided, method is called
    ///  during full pruning and notify the base block from which to keep states.
    /// Called by <see cref="Nethermind.Blockchain.FullPruning.FullPruner"/> before pruning commits,
    /// passing <c>baseBlock.Number</c>.
    /// </para>
    /// <para>
    /// When <paramref name="upToBlockNumberExclusive"/> is <see langword="null"/>,
    /// it means method is called during shutdown persistence.
    /// Called by <see cref="TrieStore"/> on shutdown.
    /// </para>
    /// </summary>
    void CopyAdditionalStatesToNodeStorage(INodeStorage target, long? upToBlockNumberExclusive = null);
}