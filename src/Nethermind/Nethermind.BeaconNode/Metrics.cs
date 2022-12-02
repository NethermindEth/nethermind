// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.BeaconNode
{
    public static class InteropMetrics
    {
        public static long LibP2PPeers { get; set; }
        public static long BeaconSlot { get; set; }
        public static long BeaconHeadSlot { get; set; }
        public static long BeaconHeadRoot { get; set; }
        public static long BeaconFinalizedEpoch { get; set; }
        public static long BeaconFinalizedRoot { get; set; }
        public static long BeaconCurrentJustifiedEpoch { get; set; }
        public static long BeaconCurrentJustifiedRoot { get; set; }
        public static long BeaconPreviousJustifiedEpoch { get; set; }
        public static long BeaconPreviousJustifiedRoot { get; set; }
    }

    public static class Metrics
    {
        public static long BeaconCurrentValidators { get; set; }
        public static long BeaconPreviousValidators { get; set; }
        public static long BeaconCurrentLiveValidators { get; set; }
        public static long BeaconPreviousLiveValidators { get; set; }
        public static long BeaconPendingDeposits { get; set; }
        public static long BeaconProcessedDepositsTotal { get; set; }
        public static long BeaconPendingExits { get; set; }
        public static long BeaconPreviousEpochOrphanedBlocks { get; set; }
        public static long BeaconReorgsTotal { get; set; }
    }
}
