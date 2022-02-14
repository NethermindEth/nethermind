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
using System;
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
        // private readonly IUserOperationTxBuilder _userOperationTxBuilder;
        // private readonly IUserOperationPool _userOperationPool;
        // private readonly IUserOperationSimulator _userOperationSimulator;

        private readonly IDictionary<Address, UserOperationTxBuilder> _userOperationTxBuilders;
        private readonly IDictionary<Address, UserOperationPool> _userOperationPools;
        private readonly IDictionary<Address, UserOperationSimulator> _userOperationSimulators;

        public UserOperationTxSource(
            IDictionary<Address, UserOperationTxBuilder> userOperationTxBuilders,
            IDictionary<Address, UserOperationPool> userOperationPools,
            IDictionary<Address, UserOperationSimulator> userOperationSimulators,
            ISpecProvider specProvider,
            ILogger logger)
        {
            _userOperationTxBuilders = userOperationTxBuilders;
            _userOperationPools = userOperationPools;
            _userOperationSimulators = userOperationSimulators;
            _specProvider = specProvider;
            _logger = logger;
        }

        public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit)
        {
            IDictionary<Address, HashSet<UInt256>> usedAccessList = new Dictionary<Address, HashSet<UInt256>>();
            // IList<UserOperation> userOperationsToInclude = new List<UserOperation>();
            IList<Tuple<Address, UserOperation>> addressedUserOperationsToInclude = new List<Tuple<Address, UserOperation>>();
            ulong gasUsed = 0;

            IList<Tuple<Address, UserOperation>> _combinedUserOperations = new List<Tuple<Address, UserOperation>>();
            foreach (Address entryPoint in _userOperationPools.Keys)
            {
                IEnumerable<UserOperation> _entryPointUserOperations = 
                    _userOperationPools[entryPoint]
                    .GetUserOperations()
                    .Where(op => op.MaxFeePerGas >= parent.BaseFeePerGas);

                foreach(UserOperation _userOperation in _entryPointUserOperations)
                {
                    _combinedUserOperations.Add(Tuple.Create(entryPoint, _userOperation));
                }
            }
            IList<Tuple<Address, UserOperation>> addressedUserOperations = _combinedUserOperations.OrderByDescending(op => CalculateUserOperationPremiumGasPrice(op.Item2, parent.BaseFeePerGas)).ToList();

            // IEnumerable<UserOperation> userOperations =
            //     _userOperationPool
            //         .GetUserOperations()
            //         .Where(op => op.MaxFeePerGas >= parent.BaseFeePerGas)
            //         .OrderByDescending(op => CalculateUserOperationPremiumGasPrice(op, parent.BaseFeePerGas));
            
            foreach (Tuple<Address, UserOperation> addressedUserOperation in addressedUserOperations)
            {
                if (gasUsed >= (ulong)gasLimit) continue;

                UserOperation userOperation = addressedUserOperation.Item2;
                Address entryPoint = addressedUserOperation.Item1;

                // no intersect of accessed addresses between ops
                if (userOperation.AccessList.AccessListOverlaps(usedAccessList)) continue;

                // simulate again to make sure the op is still valid
                ResultWrapper<Keccak> result = _userOperationSimulators[entryPoint].Simulate(userOperation, parent);
                if (result.Result != Result.Success)
                {
                    //if (_logger.IsDebug) commented out for testing
                    {
                        _logger.Debug($"UserOperation {userOperation.Hash} resimulation unsuccessful: {result.Result.Error}");
                        // TODO: Remove logging, just for testing
                        _logger.Info($"UserOperation {userOperation.Hash} resimulation unsuccessful: {result.Result.Error}");

                        bool removeResult = _userOperationPools[entryPoint].RemoveUserOperation(userOperation.Hash);
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

                addressedUserOperationsToInclude.Add(addressedUserOperation);
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

            if (addressedUserOperationsToInclude.Count == 0) return new List<Transaction>();

            IList<Transaction> userOperationTransactions = new List<Transaction>();

            foreach(Address entryPoint in _userOperationTxBuilders.Keys)
            {
                IList<UserOperation> userOperationsToInclude = new List<UserOperation>();
                foreach (Tuple<Address, UserOperation> uop in addressedUserOperationsToInclude)
                {
                    if (uop.Item1 == entryPoint)
                    userOperationsToInclude.Add(uop.Item2);
                }

                if(userOperationsToInclude.Count == 0) continue;

                Transaction userOperationTransaction =
                    _userOperationTxBuilders[entryPoint].BuildTransactionFromUserOperations(
                        userOperationsToInclude, 
                        parent, 
                        100_000_000, // high gas to test
                        _specProvider.GetSpec(parent.Number + 1));
                if (_logger.IsDebug)
                    _logger.Debug($"Constructed tx from {userOperationsToInclude.Count} userOperations: {userOperationTransaction.Hash}");
                // TODO: Remove logging, just for testing
                _logger.Info($"Constructed tx from {userOperationsToInclude.Count} userOperations: {userOperationTransaction.Hash}");

                BlockchainBridge.CallOutput callOutput = _userOperationSimulators[entryPoint].EstimateGas(parent, userOperationTransaction, CancellationToken.None);
                FailedOp? failedOp = _userOperationTxBuilders[entryPoint].DecodeEntryPointOutputError(callOutput.OutputData);
                if (failedOp is not null)
                {
                    // TODO punish paymaster
                }
                
                Transaction updatedUserOperationTransaction =
                    _userOperationTxBuilders[entryPoint].BuildTransactionFromUserOperations(
                        userOperationsToInclude, 
                        parent, 
                        callOutput.GasSpent,
                        _specProvider.GetSpec(parent.Number + 1));

                userOperationTransactions.Add(updatedUserOperationTransaction);
            }

            return userOperationTransactions;
        }

        private UInt256 CalculateUserOperationPremiumGasPrice(UserOperation op, UInt256 baseFeePerGas)
        {
            return UInt256.Min(op.MaxPriorityFeePerGas, op.MaxFeePerGas - baseFeePerGas);
        }
    }
}
