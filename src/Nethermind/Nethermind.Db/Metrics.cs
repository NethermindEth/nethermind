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

using System.ComponentModel;

namespace Nethermind.Db
{
    public static class Metrics
    {
        [Description("Number of Blocks DB reads.")]
        public static long BlocksDbReads { get; set; }
        
        [Description("Number of Blocks DB writes.")]
        public static long BlocksDbWrites { get; set; }
        
        [Description("Number of Code DB reads.")]
        public static long CodeDbReads { get; set; }
        
        [Description("Number of Code DB writes.")]
        public static long CodeDbWrites { get; set; }
        
        [Description("Number of Receipts DB reads.")]
        public static long ReceiptsDbReads { get; set; }
        
        [Description("Number of Receipts DB writes.")]
        public static long ReceiptsDbWrites { get; set; }
        
        [Description("Number of Block Infos DB reads.")]
        public static long BlockInfosDbReads { get; set; }
        
        [Description("Number of Block Infos DB writes.")]
        public static long BlockInfosDbWrites { get; set; }
        
        [Description("Number of State Trie reads.")]
        public static long StateTreeReads { get; set; }
        
        [Description("Number of Blocks Trie writes.")]
        public static long StateTreeWrites { get; set; }
        
        [Description("Number of State DB reads.")]
        public static long StateDbReads { get; set; }
        
        [Description("Number of State DB writes.")]
        public static long StateDbWrites { get; set; }
        
        [Description("Number of storge trie reads.")]
        public static long StorageTreeReads { get; set; }
        
        [Description("Number of storage trie writes.")]
        public static long StorageTreeWrites { get; set; }
        
        [Description("Number of Pending Tx DB reads.")]
        public static long PendingTxsDbReads { get; set; }
        
        [Description("Number of Pending Tx DB writes.")]
        public static long PendingTxsDbWrites { get; set; }
        
        [Description("Number of Eth Request (faucet) DB reads.")]
        public static long EthRequestsDbReads { get; set; }
        
        [Description("Number of Eth Request (faucet) DB writes.")]
        public static long EthRequestsDbWrites { get; set; }
        
        [Description("Number of other DB reads.")]
        public static long OtherDbReads { get; set; }
        
        [Description("Number of other DB writes.")]
        public static long OtherDbWrites { get; set; }
        
        
        [Description("Number of Headers DB reads.")]
        public static long HeaderDbReads { get; set; }
        
        [Description("Number of Headers DB writes.")]
        public static long HeaderDbWrites { get; set; }
    }
} 