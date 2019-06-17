/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

namespace Nethermind.Store
{
    public static class Metrics
    {
        public static long BlocksDbReads { get; set; }
        public static long BlocksDbWrites { get; set; }
        public static long CodeDbReads { get; set; }
        public static long CodeDbWrites { get; set; }
        public static long ReceiptsDbReads { get; set; }
        public static long ReceiptsDbWrites { get; set; }
        public static long BlockInfosDbReads { get; set; }
        public static long BlockInfosDbWrites { get; set; }
        public static long StateTreeReads { get; set; }
        public static long StateTreeWrites { get; set; }
        public static long StateDbReads { get; set; }
        public static long StateDbWrites { get; set; }
        public static long StorageTreeReads { get; set; }
        public static long StorageTreeWrites { get; set; }
        public static long PendingTxsDbReads { get; set; }
        public static long PendingTxsDbWrites { get; set; }
        public static long ConsumersDbReads { get; set; }
        public static long ConsumersDbWrites { get; set; }
        public static long ConfigsDbReads { get; set; }
        public static long ConfigsDbWrites { get; set; }
        public static long EthRequestsDbReads { get; set; }
        public static long EthRequestsDbWrites { get; set; }
        public static long TraceDbReads { get; set; }
        public static long TraceDbWrites { get; set; }
        public static long OtherDbReads { get; set; }
        public static long OtherDbWrites { get; set; }
        public static long TreeNodeHashCalculations { get; set; }
        public static long TreeNodeRlpEncodings { get; set; }
        public static long TreeNodeRlpDecodings { get; set; }
        public static long HeaderDbReads { get; set; }
        public static long HeaderDbWrites { get; set; }
    }
} 