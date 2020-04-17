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

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Abi;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.State;

namespace Nethermind.Consensus.AuRa.Transactions
{
    public class RandomImmediateTransactionSource : IImmediateTransactionSource
    {
        private readonly Address _nodeAddress;
        private readonly IList<RandomContract> _contracts;

        public RandomImmediateTransactionSource(
            IDictionary<long, Address> randomnessContractAddress, 
            ITransactionProcessor transactionProcessor, 
            IAbiEncoder abiEncoder, 
            IStateProvider stateProvider, 
            IReadOnlyTransactionProcessorSource readOnlyTransactionProcessorSource,
            Address nodeAddress)
        {
            _nodeAddress = nodeAddress ?? throw new ArgumentNullException(nameof(nodeAddress));
            _contracts = randomnessContractAddress
                .Select(kvp => new RandomContract(transactionProcessor, abiEncoder, kvp.Value, stateProvider, readOnlyTransactionProcessorSource, kvp.Key))
                .ToList();
        }
        
        public bool TryCreateTransaction(long blockNumber, long gasLimit, out Transaction tx)
        {
            if (_contracts.TryGetForBlock(blockNumber, out var contract))
            {
                contract.GetPhase();
            }
            
            tx = null;
            return false;
        }
    }
}