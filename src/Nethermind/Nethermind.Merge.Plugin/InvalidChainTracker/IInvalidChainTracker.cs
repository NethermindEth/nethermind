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
    /// Return last valid hash if this block is known to be on an invalid chain.
    /// Return null otherwise
    /// </summary>
    bool IsOnKnownInvalidChain(Hash256 blockHash, out Hash256? lastValidHash);
}
