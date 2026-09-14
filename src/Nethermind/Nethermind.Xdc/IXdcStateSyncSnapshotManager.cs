// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Xdc;

public interface IXdcStateSyncSnapshotManager
{
    /// <summary>Returns the gap block headers that must be downloaded to sync at the given pivot.</summary>
    /// <remarks>
    /// Headers arrive descending from the pivot, so the blocks below it may not be inserted yet. Callers are expected
    /// to retry on <c>null</c> rather than treat it as an error.
    /// </remarks>
    /// <param name="pivotHeader">The target pivot block.</param>
    /// <returns>Ordered gap block headers whose state must be downloaded, or <c>null</c> if any is not yet available.</returns>
    XdcBlockHeader[]? GetGapBlocks(XdcBlockHeader pivotHeader);

    /// <summary>Stores the snapshot for a fully synced gap block.</summary>
    /// <param name="gapBlockHeader">The completed gap block.</param>
    void StoreSnapshot(XdcBlockHeader gapBlockHeader);
}
