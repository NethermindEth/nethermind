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

using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Core
{
    public class TxReceipt
    {
        /// <summary>
        ///     EIP-658
        /// </summary>
        public byte StatusCode { get; set; }

        public long BlockNumber { get; set; }
        public Keccak BlockHash { get; set; }
        public Keccak TxHash { get; set; }
        public int Index { get; set; }
        public long GasUsed { get; set; }
        public long GasUsedTotal { get; set; }
        public Address Sender { get; set; }
        public Address ContractAddress { get; set; }
        public Address Recipient { get; set; }
        
        [Todo(Improve.Refactor, "Receipt tracer?")]
        public byte[] ReturnValue { get; set; }
        
        /// <summary>
        ///     Removed in EIP-658
        /// </summary>
        public Keccak PostTransactionState { get; set; }
        public Bloom Bloom { get; set; }
        public LogEntry[] Logs { get; set; }
        public string Error { get; set; }
    }
}