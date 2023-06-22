// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Synchronization.ParallelSync
{
    [Flags]
    public enum SyncMode
    {
        None = 0,

        /// <summary>
        /// We are connected to nodes and processing based on discovery
        /// </summary>
        WaitingForBlock = 1,
        /// <summary>
        /// We are not connected to nodes
        /// </summary>
        Disconnected = 2,
        /// <summary>
        /// Stage of fast sync that downloads headers, bodies or receipts from pivot to beginning of chain in parallel.
        /// </summary>
        FastBlocks = 4,
        /// <summary>
        /// A standard fast sync mode before the peers head - 32 (threshold). It happens after the fast blocks finishes to download from pivot downwards. By default the pivot for fast blocks is 0 so the fast blocks finish immediately.
        /// </summary>
        FastSync = 8,
        /// <summary>
        /// This is the stage of the fast sync when all the trie nodes are downloaded. The node can keep switching between StateNodes and FastSync while it has to catch up with the Head - 32 due to peers not returning old trie nodes.
        /// </summary>
        StateNodes = 16,
        /// <summary>
        /// This is either a standard full archive sync from genesis or full sync after StateNodes finish.
        /// </summary>
        Full = 32,
        /// <summary>
        /// Loading previously downloaded blocks from the DB
        /// </summary>
        DbLoad = 128,
        /// <summary>
        /// Stage of fast sync that downloads headers in parallel.
        /// </summary>
        FastHeaders = FastBlocks | 256,
        /// <summary>
        /// Stage of fast sync that downloads headers in parallel.
        /// </summary>
        FastBodies = FastBlocks | 512,
        /// <summary>
        /// Stage of fast sync that downloads headers in parallel.
        /// </summary>
        FastReceipts = FastBlocks | 1024,
        /// <summary>
        /// Stage of snap sync that state is being downloaded (accounts, storages, code, proofs)
        /// </summary>
        SnapSync = 2048,
        /// <summary>
        /// Reverse download of headers from beacon pivot to genesis
        /// </summary>
        BeaconHeaders = 4096,
        /// <summary>
        /// Waiting for Forkchoice message from Consensus Layer to update pivot block
        /// </summary>
        UpdatingPivot = 8192,

        All = WaitingForBlock | Disconnected | FastBlocks | FastSync | StateNodes | StateNodes | Full | DbLoad |
              FastHeaders | FastBodies | FastReceipts | SnapSync | BeaconHeaders | UpdatingPivot
    }

    public static class SyncModeExtensions
    {
        public static bool NotSyncing(this SyncMode syncMode) => syncMode == SyncMode.WaitingForBlock || syncMode == SyncMode.Disconnected;

        public static bool IsSyncingBodies(this SyncMode syncMode) =>
            syncMode.HasFlag(SyncMode.FastHeaders) ||
            syncMode.HasFlag(SyncMode.FastBodies) ||
            syncMode.HasFlag(SyncMode.FastSync) ||
            syncMode.HasFlag(SyncMode.StateNodes) ||
            syncMode.HasFlag(SyncMode.SnapSync) ||
            syncMode.HasFlag(SyncMode.BeaconHeaders) ||
            syncMode.HasFlag(SyncMode.UpdatingPivot);

        public static bool IsSyncingReceipts(this SyncMode syncMode) =>
            syncMode.HasFlag(SyncMode.FastBlocks) ||
            syncMode.HasFlag(SyncMode.FastSync) ||
            syncMode.HasFlag(SyncMode.StateNodes) ||
            syncMode.HasFlag(SyncMode.SnapSync) ||
            syncMode.HasFlag(SyncMode.BeaconHeaders) ||
            syncMode.HasFlag(SyncMode.UpdatingPivot);

        public static bool IsSyncingHeaders(this SyncMode syncMode) =>
            syncMode.HasFlag(SyncMode.FastHeaders) ||
            syncMode.HasFlag(SyncMode.BeaconHeaders) ||
            syncMode.HasFlag(SyncMode.UpdatingPivot);

        public static bool IsSyncingState(this SyncMode syncMode) =>
            syncMode.HasFlag(SyncMode.FastSync) ||
            syncMode.HasFlag(SyncMode.StateNodes) ||
            syncMode.HasFlag(SyncMode.SnapSync) ||
            syncMode.HasFlag(SyncMode.UpdatingPivot);
    }
}
