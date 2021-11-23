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

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.AccountAbstraction.Data;
using Nethermind.AccountAbstraction.Executor;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Logging;

namespace Nethermind.AccountAbstraction.Source
{
    public class UserOperationTxSource : ITxBundleSource
    {
        private readonly ILogger _logger;
        private readonly ISpecProvider _specProvider;
        private readonly IUserOperationPool _userOperationPool;
        private readonly IUserOperationSimulator _userOperationSimulator;

        public UserOperationTxSource(
            IUserOperationPool userOperationPool,
            IUserOperationSimulator userOperationSimulator,
            ISpecProvider specProvider,
            ILogger logger)
        {
            _userOperationPool = userOperationPool;
            _userOperationSimulator = userOperationSimulator;
            _specProvider = specProvider;
            _logger = logger;
        }

        public Transaction? GetTransaction(BlockHeader head, ulong gasLimit)
        {
            IDictionary<Address, HashSet<UInt256>> usedAccessList = new Dictionary<Address, HashSet<UInt256>>();
            IList<UserOperation> userOperationsToInclude = new List<UserOperation>();
            ulong gasUsed = 0;

            IEnumerable<UserOperation> userOperations =
                _userOperationPool
                    .GetUserOperations()
                    .Where(op => op.MaxFeePerGas >= head.BaseFeePerGas)
                    .OrderByDescending(op => CalculateUserOperationPremiumGasPrice(op, head.BaseFeePerGas));
            foreach (UserOperation userOperation in userOperations)
            {
                if (gasUsed >= (ulong)gasLimit) continue;

                // no intersect of accessed addresses between ops
                if (UserOperationAccessList.AccessListOverlaps(usedAccessList, userOperation.AccessList.Data)) continue;

                // simulate again to make sure the op is still valid
                Task<ResultWrapper<Keccak>> resultTask = _userOperationSimulator.Simulate(userOperation, head);
                ResultWrapper<Keccak> result = resultTask.Result;
                if (result.Result != Result.Success)
                {
                    //if (_logger.IsDebug) commented out for testing
                    {
                        _logger.Debug($"UserOperation {userOperation.Hash} resimulation unsuccessful: {result.Result.Error}");

                        // ToDo: RemoveUserOperation shouldn't be dependent of logger's state, like below. Commented it out for now
                        // _logger.Debug(_userOperationPool.RemoveUserOperation(userOperation.Hash)
                        //     ? $"Removed UserOperation {userOperation.Hash} from Pool"
                        //     : $"Failed to remove UserOperation {userOperation} from Pool");
                    }

                    continue;
                }

                userOperationsToInclude.Add(userOperation);
                gasUsed += (ulong)userOperation.CallGas +
                           (ulong)userOperation.PreVerificationGas +
                           (ulong)userOperation.VerificationGas;

                // add userOp accessList to combined list
                foreach (KeyValuePair<Address, HashSet<UInt256>> kv in userOperation.AccessList.Data)
                    if (usedAccessList.ContainsKey(kv.Key))
                        usedAccessList[kv.Key].UnionWith(kv.Value);
                    else
                        usedAccessList[kv.Key] = kv.Value;
            }

            if (userOperationsToInclude.Count == 0) return null;

            Transaction userOperationTransaction =
                _userOperationSimulator.BuildTransactionFromUserOperations(userOperationsToInclude, head,
                    _specProvider.GetSpec(head.Number + 1));
            if (_logger.IsDebug)
                _logger.Debug($"Constructed tx from {userOperationsToInclude.Count} userOperations: {userOperationTransaction.Hash}");
            return userOperationTransaction;
        }

        private UInt256 CalculateUserOperationPremiumGasPrice(UserOperation op, UInt256 baseFeePerGas)
        {
            return UInt256.Min(op.MaxPriorityFeePerGas, op.MaxFeePerGas - baseFeePerGas);
        }
    }
}
