//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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