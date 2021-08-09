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
// 

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Nethermind.AccountAbstraction.Data;
using Nethermind.AccountAbstraction.Executor;
using Nethermind.Consensus;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;

namespace Nethermind.AccountAbstraction.Source
{
    public class UserOperationTxSource : ITxSource
    {
        private readonly IUserOperationPool _userOperationPool;
        private readonly ConcurrentDictionary<UserOperation, SimulatedUserOperation> _simulatedUserOperations;
        private readonly IUserOperationSimulator _userOperationSimulator;

        public UserOperationTxSource(IUserOperationPool userOperationPool, 
            ConcurrentDictionary<UserOperation, SimulatedUserOperation> simulatedUserOperations,
            IUserOperationSimulator userOperationSimulator)
        {
            _userOperationPool = userOperationPool;
            _simulatedUserOperations = simulatedUserOperations;
            _userOperationSimulator = userOperationSimulator;
        }

        public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit)
        {
            IList<Address> usedAddresses = new List<Address>();
            IList<UserOperation> userOperations = new List<UserOperation>();
            long gasUsed = 0;
            
            IEnumerable<SimulatedUserOperation> simulatedUserOperations = _simulatedUserOperations.Values.OrderByDescending(op => op.ImpliedGasPrice);
            foreach (SimulatedUserOperation simulatedUserOperation in simulatedUserOperations)
            {
                if (gasUsed >= gasLimit)
                {
                    break;
                }
                
                // no intersect of accessed addresses
                if (usedAddresses.Intersect(simulatedUserOperation.UserOperation.AccessList.Data.Keys).Any())
                {
                    break;
                }
                
                userOperations.Add(simulatedUserOperation.UserOperation);
                gasUsed += simulatedUserOperation.UserOperation.CallGas; // TODO FIX THIS AFTER WE FIGURE OUT HOW CONTRACT WORKS
            }

            Transaction userOperationTransaction = _userOperationSimulator.BuildTransactionFromUserOperations(userOperations, parent);
            return new List<Transaction>{userOperationTransaction};
        }
    }
}
