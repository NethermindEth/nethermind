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

namespace Nethermind.PubSub.Models
{
    public class TransactionReceipt
    {
        public byte StatusCode { get; set; }
        public string BlockNumber { get; set; }
        public byte[] BlockHash { get; set; }
        public byte[] TransactionHash { get; set; }
        public int Index { get; set; }
        public long GasUsed { get; set; }
        public long GasUsedTotal { get; set; }
        public byte[] Sender { get; set; }
        public byte[] ContractAddress { get; set; }
        public byte[] Recipient { get; set; }
        public byte[] PostTransactionState { get; set; }
        public byte[] Bloom { get; set; }
        public LogEntry[] Logs { get; set; }
        public string Error { get; set; }
    }
}