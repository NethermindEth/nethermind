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

    /// <summary>
    /// Moves the pivot in response to a streak of unusable range responses, but only when the head has moved
    /// at least <see cref="Blockchain.Synchronization.ISyncConfig.StateMinDistanceFromHead"/> blocks past it.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="UpdateHeaderForcefully"/>, which serves callers that need the newest state root, this
    /// path pays for the move by invalidating every in-flight and queued range, so it is rate-limited by chain
    /// progress to stop a fast chain turning the failure response into a self-sustaining livelock.
    /// </remarks>
    void UpdateHeaderAfterFailureStreak();
    ConcurrentHashSet<Hash256> UpdatedStorages { get; }
    ulong Diff { get; }
    /// <summary>Returns <c>true</c> if state sync can be finalized at <paramref name="pivot"/>.</summary>
    /// <param name="pivot">The proposed finalization point.</param>
    /// <returns><c>true</c> if ready to finalize; otherwise <c>false</c>.</returns>
    bool CanFinalize(BlockHeader pivot);
}
