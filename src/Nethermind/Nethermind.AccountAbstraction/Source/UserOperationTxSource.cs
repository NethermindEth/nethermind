// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System;
using Nethermind.AccountAbstraction.Data;
using Nethermind.AccountAbstraction.Executor;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Facade;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Core.Extensions;

namespace Nethermind.AccountAbstraction.Source
{
    public class UserOperationTxSource : ITxSource
    {
        private readonly ILogger _logger;
        private readonly ISpecProvider _specProvider;
        private readonly IReadOnlyStateProvider _stateProvider;
        private readonly ISigner _signer;

        // private readonly IUserOperationTxBuilder _userOperationTxBuilder;
        // private readonly IUserOperationPool _userOperationPool;
        // private readonly IUserOperationSimulator _userOperationSimulator;

        private readonly IDictionary<Address, UserOperationTxBuilder> _userOperationTxBuilders;
        private readonly IDictionary<Address, IUserOperationPool> _userOperationPools;
        private readonly IDictionary<Address, UserOperationSimulator> _userOperationSimulators;

        public UserOperationTxSource(
            IDictionary<Address, UserOperationTxBuilder> userOperationTxBuilders,
            IDictionary<Address, IUserOperationPool> userOperationPools,
            IDictionary<Address, UserOperationSimulator> userOperationSimulators,
            ISpecProvider specProvider,
            IReadOnlyStateProvider stateProvider,
            ISigner signer,
            ILogger logger)
        {
            _userOperationTxBuilders = userOperationTxBuilders;
            _userOperationPools = userOperationPools;
            _userOperationSimulators = userOperationSimulators;
            _specProvider = specProvider;
            _stateProvider = stateProvider;
            _signer = signer;
            _logger = logger;
        }

        public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit, PayloadAttributes? payloadAttributes)
        {
            IDictionary<Address, HashSet<UInt256>> usedAccessList = new Dictionary<Address, HashSet<UInt256>>();
            // IList<UserOperation> userOperationsToInclude = new List<UserOperation>();
            IDictionary<Address, IList<UserOperation>> userOperationsToIncludeByEntryPoint =
                new Dictionary<Address, IList<UserOperation>>();
            ulong gasUsed = 0;

            IList<Tuple<Address, UserOperation>> combinedUserOperations = new List<Tuple<Address, UserOperation>>();
            foreach (Address entryPoint in _userOperationPools.Keys)
            {
                IEnumerable<UserOperation> entryPointUserOperations =
                    _userOperationPools[entryPoint]
                    .GetUserOperations()
                    .Where(op => op.MaxFeePerGas >= parent.BaseFeePerGas);

                foreach (UserOperation userOperation in entryPointUserOperations)
                {
                    combinedUserOperations.Add(Tuple.Create(entryPoint, userOperation));
                }
            }
            IList<Tuple<Address, UserOperation>> sortedUserOperations =
                combinedUserOperations.OrderByDescending(
                op =>
                    CalculateUserOperationPremiumGasPrice(op.Item2, parent.BaseFeePerGas))
                .ToList();

            foreach (Tuple<Address, UserOperation> addressedUserOperation in sortedUserOperations)
            {
                (Address entryPoint, UserOperation userOperation) = addressedUserOperation;

                ulong userOperationTotalGasLimit = (ulong)userOperation.CallGas +
                                                   (ulong)userOperation.PreVerificationGas +
                                                   (ulong)userOperation.VerificationGas;

                if (gasUsed + userOperationTotalGasLimit > (ulong)gasLimit) continue;

                // no intersect of accessed addresses between ops
                if (userOperation.AccessList.AccessListOverlaps(usedAccessList)) continue;

                // simulate again to make sure the op is still valid
                ResultWrapper<Keccak> result = _userOperationSimulators[entryPoint].Simulate(userOperation, parent);
                if (result.Result != Result.Success)
                {
                    //if (_logger.IsDebug) commented out for testing
                    {
                        _logger.Debug($"UserOperation {userOperation.RequestId!} resimulation unsuccessful: {result.Result.Error}");
                        // TODO: Remove logging, just for testing
                        _logger.Info($"UserOperation {userOperation.RequestId!} resimulation unsuccessful: {result.Result.Error}");

                        bool removeResult = _userOperationPools[entryPoint].RemoveUserOperation(userOperation.RequestId!);
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

                gasUsed += userOperationTotalGasLimit;

                // add user operation with correct entryPoint
                if (userOperationsToIncludeByEntryPoint.TryGetValue(entryPoint, out IList<UserOperation>? userOperations))
                {
                    userOperations.Add(userOperation);
                }
                else
                {
                    userOperationsToIncludeByEntryPoint[entryPoint] = new List<UserOperation> { userOperation };
                }

                // add userOp accessList to combined list
                foreach (KeyValuePair<Address, HashSet<UInt256>> kv in userOperation.AccessList.Data)
                    if (usedAccessList.TryGetValue(kv.Key, out HashSet<UInt256>? value))
                        value.UnionWith(kv.Value);
                    else
                        usedAccessList[kv.Key] = kv.Value;
            }

            if (userOperationsToIncludeByEntryPoint.Count == 0) yield break;

            UInt256 initialNonce = _stateProvider.GetNonce(_signer.Address);
            UInt256 txsBuilt = 0;
            // build transaction for each entryPoint with ops to be included
            foreach (KeyValuePair<Address, UserOperationTxBuilder> kv in _userOperationTxBuilders)
            {
                Address entryPoint = kv.Key;
                IUserOperationTxBuilder txBuilder = kv.Value;

                bool foundUserOperations =
                    userOperationsToIncludeByEntryPoint.TryGetValue(entryPoint, out IList<UserOperation>? userOperationsToInclude);
                if (!foundUserOperations) continue;

                long totalGasUsed = userOperationsToInclude!.Aggregate((long)0,
                    (sum, op) =>
                        sum +
                        (long)op.CallGas +
                        (long)op.PreVerificationGas +
                        (long)op.VerificationGas);

                // build test transaction to make sure it succeeds as a batch of ops
                Transaction userOperationTransaction =
                    txBuilder.BuildTransactionFromUserOperations(
                        userOperationsToInclude!,
                        parent,
                        totalGasUsed,
                        initialNonce,
                        _specProvider.GetSpecFor1559(parent.Number + 1));
                if (_logger.IsDebug)
                    _logger.Debug($"Constructed tx from {userOperationsToInclude!.Count} userOperations: {userOperationTransaction.Hash}");
                // TODO: Remove logging, just for testing
                _logger.Info($"Constructed tx from {userOperationsToInclude!.Count} userOperations: {userOperationTransaction.Hash}");

                BlockchainBridge.CallOutput callOutput = _userOperationSimulators[entryPoint].EstimateGas(parent, userOperationTransaction, CancellationToken.None);
                FailedOp? failedOp = txBuilder.DecodeEntryPointOutputError(callOutput.OutputData);
                if (failedOp is not null)
                {
                    UserOperation opToRemove = userOperationsToInclude[(int)failedOp.Value._opIndex];
                    _userOperationPools[entryPoint].RemoveUserOperation(opToRemove.RequestId!);
                    continue;
                }
                if (callOutput.Error is not null)
                {
                    if (_logger.IsWarn) _logger.Warn($"AA Simulation error for entryPoint {entryPoint}: {callOutput.Error}");
                    continue;
                }

                // construct tx with previously estimated gas limit
                Transaction updatedUserOperationTransaction =
                    _userOperationTxBuilders[entryPoint].BuildTransactionFromUserOperations(
                        userOperationsToInclude,
                        parent,
                        callOutput.GasSpent + 200000,
                        initialNonce + txsBuilt,
                        _specProvider.GetSpecFor1559(parent.Number + 1));

                txsBuilt++;
                yield return updatedUserOperationTransaction;
            }
        }

        private UInt256 CalculateUserOperationPremiumGasPrice(UserOperation op, UInt256 baseFeePerGas)
        {
            return UInt256.Min(op.MaxPriorityFeePerGas, op.MaxFeePerGas - baseFeePerGas);
        }
    }
}
