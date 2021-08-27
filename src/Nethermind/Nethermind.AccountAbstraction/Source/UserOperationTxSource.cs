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
using System.Threading.Tasks;
using Nethermind.AccountAbstraction.Data;
using Nethermind.AccountAbstraction.Executor;
using Nethermind.Consensus;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.AccountAbstraction.Source
{
    public class UserOperationTxSource : ITxSource
    {
        private readonly IUserOperationPool _userOperationPool;
        private readonly IUserOperationSimulator _userOperationSimulator;

        public UserOperationTxSource(
            IUserOperationPool userOperationPool, 
            IUserOperationSimulator userOperationSimulator)
        {
            _userOperationPool = userOperationPool;
            _userOperationSimulator = userOperationSimulator;
        }

        public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit)
        {
            IDictionary<Address, HashSet<UInt256>> usedAccessList = new Dictionary<Address, HashSet<UInt256>>();
            IList<UserOperation> userOperationsToInclude = new List<UserOperation>();
            ulong gasUsed = 0;
            
            IEnumerable<UserOperation> userOperations = _userOperationPool.GetUserOperations().Where(op => op.MaxFeePerGas >= parent.BaseFeePerGas).OrderByDescending(op => CalculateUserOperationPremiumGasPrice(op, parent.BaseFeePerGas));
            foreach (UserOperation userOperation in userOperations)
            {
                if (gasUsed >= (ulong)gasLimit)
                {
                    continue;
                }
                
                // no intersect of accessed addresses
                if (UserOperationPool.AccessListOverlaps(usedAccessList, userOperation.AccessList.Data))
                {
                    continue;
                }

                if (userOperation.AccessListTouched)
                {
                    Task<bool> successTask = _userOperationSimulator.Simulate(userOperation, parent);
                    bool success = successTask.Result;
                    if (!success)
                    {
                        // implement flow here
                    }
                }
                
                userOperationsToInclude.Add(userOperation);
                gasUsed += (ulong) userOperation.CallGas; // TODO FIX THIS AFTER WE FIGURE OUT HOW CONTRACT WORKS
                
                foreach (var kv in userOperation.AccessList.Data)
                {
                    if (usedAccessList.ContainsKey(kv.Key))
                    {
                        usedAccessList[kv.Key].UnionWith(kv.Value);
                    }
                    else
                    {
                        usedAccessList[kv.Key] = (HashSet<UInt256>) kv.Value;
                    }
                }
            }

            if (userOperationsToInclude.Count == 0)
            {
                return new List<Transaction>();
            }
            
            Transaction userOperationTransaction = _userOperationSimulator.BuildSimulateTransactionFromUserOperations(userOperationsToInclude[0], parent);
            return new List<Transaction>{userOperationTransaction};
        }

        private UInt256 CalculateUserOperationPremiumGasPrice(UserOperation op, UInt256 baseFeePerGas)
        {
            return UInt256.Min(op.MaxPriorityFeePerGas, op.MaxFeePerGas - baseFeePerGas);
        }
    }
}
