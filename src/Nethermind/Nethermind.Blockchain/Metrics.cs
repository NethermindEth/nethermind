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

namespace Nethermind.Blockchain
{
    public static class Metrics
    {
        public static decimal Mgas { get; set; }
        public static long Transactions { get; set; }
        public static long Blocks { get; set; }
        public static long Reorganizations { get; set; }
        public static long RecoveryQueueSize { get; set; }
        public static long ProcessingQueueSize { get; set; }
        public static long SyncPeers { get; set; }
        public static long PendingTransactionsSent { get; set; }
        public static long PendingTransactionsReceived { get; set; }
        public static long PendingTransactionsDiscarded { get; set; }
        public static long PendingTransactionsKnown { get; set; }
    }
}