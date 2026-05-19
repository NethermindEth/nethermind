// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using ConcurrentCollections;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Synchronization.FastSync;

public interface IStateSyncPivot
{
    BlockHeader? GetPivotHeader();
    void UpdateHeaderForcefully();
    ConcurrentHashSet<Hash256> UpdatedStorages { get; }
    long Diff { get; }
    /// <summary>
    /// The very first pivot snap sync downloaded against. Captured the first time
    /// the pivot header is set and never advances after that, so BAL healing can
    /// pin trie reassembly to the state snap sync actually wrote to disk.
    /// </summary>
    BlockHeader? FirstPivot { get; }
    /// <summary>Returns <c>true</c> if state sync can be finalized at <paramref name="pivot"/>.</summary>
    /// <param name="pivot">The proposed finalization point.</param>
    /// <returns><c>true</c> if ready to finalize; otherwise <c>false</c>.</returns>
    bool CanFinalize(BlockHeader pivot);
}
