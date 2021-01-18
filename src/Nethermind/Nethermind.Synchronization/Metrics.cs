//  Copyright (c) 2021 Demerzel Solutions Limited
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

using System.ComponentModel;

namespace Nethermind.Synchronization
{
    public static class Metrics
    {
        [Description("Headers downloaded in fast blocks stage")]
        public static decimal FastHeaders;

        [Description("Bodies downloaded in fast blocks stage")]
        public static long FastBodies;

        [Description("Receipts downloaded in fast blocks stage")]
        public static long FastReceipts;
        
        [Description("State synced in bytes")]
        public static long StateSynced;

        [Description("Transactions processed by the beam processor")]
        public static long BeamedTransactions;

        [Description("Full blocks processed by the beam processor")]
        public static long BeamedBlocks;

        [Description("Requests sent for state nodes sync")]
        public static long StateSyncRequests;
        
        [Description("State trie nodes synced")]
        public static long SyncedStateTrieNodes;
        
        [Description("Storage trie nodes synced")]
        public static long SyncedStorageTrieNodes;
        
        [Description("Synced bytecodes")]
        public static long SyncedCodes;
        
        [Description("Requests sent for processing by the beam sync DB")]
        public static long BeamedRequests;

        [Description("Trie nodes retrieved via beam sync DB")]
        public static long BeamedTrieNodes;

        [Description("Number of sync peers.")]
        public static long SyncPeers;

        [Description("State branch progress (percentage of completed branches at second level).")]
        public static long StateBranchProgress;
        
        [Description("Requests sent for processing by the witness state sync")]
        public static long WitnessStateRequests;
        
        [Description("Requests sent for processing by the witness block sync")]
        public static long WitnessBlockRequests;
    }
}
