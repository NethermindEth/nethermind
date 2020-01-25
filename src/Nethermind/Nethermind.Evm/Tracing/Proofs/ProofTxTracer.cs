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

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Store;

namespace Nethermind.Evm.Tracing.Proofs
{
    public class ProofTxTracer : ITxTracer
    {
        public HashSet<Address> Accounts { get; } = new HashSet<Address>();

        public HashSet<StorageAddress> Storages { get; } = new HashSet<StorageAddress>();

        public HashSet<Keccak> BlockHashes { get; } = new HashSet<Keccak>();

        public byte[] Output { get; private set; }

        public bool IsTracingBlockHash => true;
        public bool IsTracingReceipt => true;
        public bool IsTracingState => true;

        public void ReportBlockHash(Keccak blockHash)
        {
            BlockHashes.Add(blockHash);
        }

        public void ReportBalanceChange(Address address, UInt256? before, UInt256? after)
        {
            Accounts.Add(address);
        }

        public void ReportCodeChange(Address address, byte[] before, byte[] after)
        {
            Accounts.Add(address);
        }

        public void ReportNonceChange(Address address, UInt256? before, UInt256? after)
        {
            Accounts.Add(address);
        }

        public void ReportStorageChange(StorageAddress storageAddress, byte[] before, byte[] after)
        {
            Accounts.Add(storageAddress.Address);
            Storages.Add(storageAddress);
        }
        
        public void ReportAccountRead(Address address)
        {
            Accounts.Add(address);
        }

        public void MarkAsSuccess(Address recipient, long gasSpent, byte[] output, LogEntry[] logs, Keccak stateRoot = null)
        {
            Output = output;
        }

        public void MarkAsFailed(Address recipient, long gasSpent, byte[] output, string error, Keccak stateRoot = null)
        {
            Output = output;
        }
    }
}