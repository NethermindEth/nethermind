// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Xdc;

public interface IXdcStateSyncSnapshotManager
{
    /// <summary>Returns the gap block headers that must be downloaded to sync at the given pivot.</summary>
    /// <param name="pivotHeader">The target pivot block.</param>
    /// <returns>Ordered gap block headers whose state must be downloaded.</returns>
    XdcBlockHeader[] GetGapBlocks(XdcBlockHeader pivotHeader);

    /// <summary>Stores the snapshot for a fully synced gap block.</summary>
    /// <param name="gapBlockHeader">The completed gap block.</param>
    void StoreSnapshot(XdcBlockHeader gapBlockHeader);
}
