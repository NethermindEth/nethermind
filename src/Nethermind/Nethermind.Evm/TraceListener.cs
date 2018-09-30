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

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.Evm
{
    public class TraceListener : ITraceListener
    {
        public TransactionTrace Trace { get; private set; }

        private readonly Keccak _txHash;

        public TraceListener(Keccak txHash)
        {
            _txHash = txHash;
        }

        public bool ShouldTrace(Keccak txHash)
        {
            return txHash == _txHash;
        }

        public void RecordTrace(Keccak txHash, TransactionTrace trace)
        {
            if (_txHash != txHash)
            {
                throw new InvalidOperationException($"Received a trace for {txHash} while only interested in {_txHash}");
            }

            Trace = trace;
        }
    }
}