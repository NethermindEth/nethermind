// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Verkle;

namespace Nethermind.Verkle.Tree.Sync;

public class SubTreeRange(Hash256 stateRoot, Stem startingStem, Stem? limitStem = null, long? blockNumber = null)
{
    public long? BlockNumber { get; } = blockNumber;

    /// <summary>
    ///     State Root of the verkle trie to serve
    /// </summary>
    public Hash256 RootHash { get; } = stateRoot;

    /// <summary>
    ///     Stem of the first sub-tree to retrieve
    /// </summary>
    public Stem StartingStem { get; } = startingStem;

    /// <summary>
    ///     Stem after which to stop serving data
    /// </summary>
    public Stem? LimitStem { get; } = limitStem;

    public override string ToString()
    {
        return $"SubTreeRange: (BN:{BlockNumber}.RH:{RootHash}, Stem.S:{StartingStem}.L:{LimitStem})";
    }
}
