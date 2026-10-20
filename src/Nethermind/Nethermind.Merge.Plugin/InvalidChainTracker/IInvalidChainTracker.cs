// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.Merge.Plugin.InvalidChainTracker;

public interface IInvalidChainTracker : IDisposable
{
    /// <summary>
    /// Suggest that these hash are child parent of each other. Used to determine if a hash is on an invalid chain
    /// </summary>
    /// <param name="child"></param>
    /// <param name="parent"></param>
    void SetChildParent(Hash256 child, Hash256 parent);

    /// <summary>
    /// Mark the block hash as a failed block.
    /// </summary>
    /// <param name="failedBlock"></param>
    /// <param name="parent"></param>
    void OnInvalidBlock(Hash256 failedBlock, Hash256? parent);

    /// <summary>
    /// Whether this block is known to be on an invalid chain. <paramref name="lastValidHash"/> is then the hash of
    /// its most recent fully validated ancestor, zero for a pre-merge one, or null when none is known.
    /// </summary>
    bool IsOnKnownInvalidChain(Hash256 blockHash, out Hash256? lastValidHash);
}
