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
using System.Threading;
using System.Threading.Tasks;
using Nethermind.AccountAbstraction.Data;
using Nethermind.AccountAbstraction.Executor;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Facade;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Logging;

namespace Nethermind.AccountAbstraction.Source
{
    public class UserOperationTxSource : ITxSource
    {
        private readonly ILogger _logger;
        private readonly ISpecProvider _specProvider;
        private readonly IUserOperationTxBuilder _userOperationTxBuilder;
        private readonly IUserOperationPool _userOperationPool;
        private readonly IUserOperationSimulator _userOperationSimulator;

        public UserOperationTxSource(
            IUserOperationTxBuilder userOperationTxBuilder,
            IUserOperationPool userOperationPool,
            IUserOperationSimulator userOperationSimulator,
            ISpecProvider specProvider,
            ILogger logger)
        {
            _userOperationTxBuilder = userOperationTxBuilder;
            _userOperationPool = userOperationPool;
            _userOperationSimulator = userOperationSimulator;
            _specProvider = specProvider;
            _logger = logger;
        }

        public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit)
        {
            IDictionary<Address, HashSet<UInt256>> usedAccessList = new Dictionary<Address, HashSet<UInt256>>();
            IList<UserOperation> userOperationsToInclude = new List<UserOperation>();
            ulong gasUsed = 0;

            IEnumerable<UserOperation> userOperations =
                _userOperationPool
                    .GetUserOperations()
                    .Where(op => op.MaxFeePerGas >= parent.BaseFeePerGas)
                    .OrderByDescending(op => CalculateUserOperationPremiumGasPrice(op, parent.BaseFeePerGas));
            foreach (UserOperation userOperation in userOperations)
            {
                if (gasUsed >= (ulong)gasLimit) continue;

                // no intersect of accessed addresses between ops
                if (userOperation.AccessList.AccessListOverlaps(usedAccessList)) continue;

                // simulate again to make sure the op is still valid
                ResultWrapper<Keccak> result = _userOperationSimulator.Simulate(userOperation, parent);
                if (result.Result != Result.Success)
                {
                    //if (_logger.IsDebug) commented out for testing
                    {
                        _logger.Debug($"UserOperation {userOperation.Hash} resimulation unsuccessful: {result.Result.Error}");
                        // TODO: Remove logging, just for testing
                        _logger.Info($"UserOperation {userOperation.Hash} resimulation unsuccessful: {result.Result.Error}");

                        bool removeResult = _userOperationPool.RemoveUserOperation(userOperation.Hash);
                        if (_logger.IsDebug)
                        {
                            _logger.Debug(
                                removeResult ? 
                                "Removed UserOperation {userOperation.Hash} from Pool" 
                                : "Failed to remove UserOperation {userOperation} from Pool");
                        }
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

            if (userOperationsToInclude.Count == 0) return new List<Transaction>();

            Transaction userOperationTransaction =
                _userOperationTxBuilder.BuildTransactionFromUserOperations(
                    userOperationsToInclude, 
                    parent, 
                    100_000_000, // high gas to test
                    _specProvider.GetSpec(parent.Number + 1));
            if (_logger.IsDebug)
                _logger.Debug($"Constructed tx from {userOperationsToInclude.Count} userOperations: {userOperationTransaction.Hash}");
            // TODO: Remove logging, just for testing
            _logger.Info($"Constructed tx from {userOperationsToInclude.Count} userOperations: {userOperationTransaction.Hash}");

            BlockchainBridge.CallOutput callOutput = _userOperationSimulator.EstimateGas(parent, userOperationTransaction, CancellationToken.None);
            FailedOp? failedOp = _userOperationTxBuilder.DecodeEntryPointOutputError(callOutput.OutputData);
            if (failedOp is null)
            {
                // TODO punish paymaster
            }
            
            Transaction updatedUserOperationTransaction =
                _userOperationTxBuilder.BuildTransactionFromUserOperations(
                    userOperationsToInclude, 
                    parent, 
                    callOutput.GasSpent,
                    _specProvider.GetSpec(parent.Number + 1));

            return new List<Transaction> { updatedUserOperationTransaction };
        }

        private UInt256 CalculateUserOperationPremiumGasPrice(UserOperation op, UInt256 baseFeePerGas)
        {
            return UInt256.Min(op.MaxPriorityFeePerGas, op.MaxFeePerGas - baseFeePerGas);
        }
    }
}
