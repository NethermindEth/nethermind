// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Facade;
using Nethermind.Facade.Proxy.Models.MultiCall;
using Nethermind.Int256;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Eth.Multicall;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;

namespace Nethermind.JsonRpc.Modules.Eth
{
    public partial class EthRpcModule
    {
        private abstract class TxExecutor<TResult>
        {
            protected readonly IBlockchainBridge _blockchainBridge;
            private readonly IBlockFinder _blockFinder;
            private readonly IJsonRpcConfig _rpcConfig;

            protected TxExecutor(IBlockchainBridge blockchainBridge, IBlockFinder blockFinder, IJsonRpcConfig rpcConfig)
            {
                _blockchainBridge = blockchainBridge;
                _blockFinder = blockFinder;
                _rpcConfig = rpcConfig;
            }

            public ResultWrapper<TResult> ExecuteTx(
                TransactionForRpc transactionCall,
                BlockParameter? blockParameter)
            {
                SearchResult<BlockHeader> searchResult = _blockFinder.SearchForHeader(blockParameter);
                if (searchResult.IsError)
                {
                    return ResultWrapper<TResult>.Fail(searchResult);
                }

                BlockHeader header = searchResult.Object;
                if (!HasStateForBlock(_blockchainBridge, header))
                {
                    return ResultWrapper<TResult>.Fail($"No state available for block {header.Hash}",
                        ErrorCodes.ResourceUnavailable);
                }

                transactionCall.EnsureDefaults(_rpcConfig.GasCap);

                using CancellationTokenSource cancellationTokenSource = new(_rpcConfig.Timeout);
                Transaction tx = transactionCall.ToTransaction(_blockchainBridge.GetChainId());
                return ExecuteTx(header.Clone(), tx, cancellationTokenSource.Token);
            }

            protected abstract ResultWrapper<TResult> ExecuteTx(BlockHeader header, Transaction tx, CancellationToken token);

            protected ResultWrapper<TResult> GetInputError(BlockchainBridge.CallOutput result) =>
                ResultWrapper<TResult>.Fail(result.Error, ErrorCodes.InvalidInput);
        }

        private class CallTxExecutor : TxExecutor<string>
        {
            public CallTxExecutor(IBlockchainBridge blockchainBridge, IBlockFinder blockFinder, IJsonRpcConfig rpcConfig)
                : base(blockchainBridge, blockFinder, rpcConfig)
            {
            }

            protected override ResultWrapper<string> ExecuteTx(BlockHeader header, Transaction tx, CancellationToken token)
            {
                BlockchainBridge.CallOutput result = _blockchainBridge.Call(header, tx, token);

                if (result.Error is null)
                {
                    return ResultWrapper<string>.Success(result.OutputData.ToHexString(true));
                }

                return result.InputError
                    ? GetInputError(result)
                    : ResultWrapper<string>.Fail("VM execution error.", ErrorCodes.ExecutionError, result.Error);
            }
        }

        internal class MultiCallTxExecutor
        {
            private readonly IDbProvider _dbProvider;
            private readonly ISpecProvider _specProvider;

            internal static void ModifyAccounts(MultiCallBlockStateCallsModel requestBlockOne, IWorldState _stateProvider,
      IReleaseSpec latestBlockSpec, ISpecProvider _specProvider, MultiCallVirtualMachine virtualMachine)
            {
                Account? acc;
                foreach (AccountOverride accountOverride in requestBlockOne.StateOverrides)
                {
                    Address address = accountOverride.Address;
                    bool accExists = _stateProvider.AccountExists(address);
                    if (!accExists)
                    {
                        _stateProvider.CreateAccount(address, accountOverride.Balance, accountOverride.Nonce);
                        acc = _stateProvider.GetAccount(address);
                    }
                    else
                    {
                        acc = _stateProvider.GetAccount(address);
                    }

                    UInt256 accBalance = acc.Balance;
                    if (accBalance > accountOverride.Balance)
                    {
                        _stateProvider.SubtractFromBalance(address, accBalance - accountOverride.Balance, latestBlockSpec);
                    }
                    else if (accBalance < accountOverride.Nonce)
                    {
                        _stateProvider.AddToBalance(address, accountOverride.Balance - accBalance, latestBlockSpec);
                    }

                    UInt256 accNonce = acc.Nonce;
                    if (accNonce > accountOverride.Nonce)
                    {
                        _stateProvider.DecrementNonce(address);
                    }
                    else if (accNonce < accountOverride.Nonce)
                    {
                        _stateProvider.IncrementNonce(address);
                    }

                    if (acc != null)
                        if (accountOverride.Code is not null)
                            virtualMachine.SetOverwrite(_stateProvider, latestBlockSpec, address, new CodeInfo(accountOverride.Code), accountOverride.MoveToAddress);


                    if (accountOverride.State is not null)
                    {
                        accountOverride.State = new Dictionary<UInt256, byte[]>();
                        foreach (KeyValuePair<UInt256, byte[]> storage in accountOverride.State)
                            _stateProvider.Set(new StorageCell(address, storage.Key),
                                storage.Value.WithoutLeadingZeros().ToArray());
                    }

                    if (accountOverride.StateDiff is not null)
                    {
                        foreach (KeyValuePair<UInt256, byte[]> storage in accountOverride.StateDiff)
                            _stateProvider.Set(new StorageCell(address, storage.Key),
                                storage.Value.WithoutLeadingZeros().ToArray());
                    }

                    _stateProvider.Commit(latestBlockSpec);
                }
            }

            public MultiCallTxExecutor(IDbProvider DbProvider, ISpecProvider specProvider, IJsonRpcConfig rpcConfig)
            {
                _dbProvider = DbProvider;
                _specProvider = specProvider;
            }

            //ToDO move to exctensions
            private static IEnumerable<UInt256> Range(UInt256 start, UInt256 end)
            {
                for (var i = start; i <= end; i++)
                {
                    yield return i;
                }
            }

            public ResultWrapper<MultiCallBlockResult[]> Execute(ulong version, MultiCallBlockStateCallsModel[] blockCallsToProcess,
                BlockParameter? blockParameter)
            {
                List<MultiCallBlockStateCallsModel> blockCalls = FillInMisingBlocks(blockCallsToProcess);

                using (MultiCallBlockchainFork tmpChain = new(_dbProvider, _specProvider))
                {
                    //TODO: remove assumption that we start from head and get back
                    foreach (MultiCallBlockStateCallsModel blockCall in blockCalls)
                    {
                        bool processed = tmpChain.ForgeChainBlock((stateProvider, currentSpec, specProvider, virtualMachine) =>
                        {
                            ModifyAccounts(blockCall, stateProvider, currentSpec, specProvider, virtualMachine);
                        });
                        if (!processed)
                        {
                            break;
                        }
                    }

                }


                throw new NotImplementedException();

                /*
                BlockchainBridge.CallOutput result = _blockchainBridge.Call(header, tx, token);

                if (result.Error is null)
                {
                    return ResultWrapper<MultiCallResultModel>.Success(result.OutputData.ToHexString(true));
                }

                return result.InputError
                    ? GetInputError(result)
                    : ResultWrapper<MultiCallResultModel>.Fail("VM execution error.", ErrorCodes.ExecutionError, result.Error);
                */
            }

            //Returns an ordered list of blockCallsToProcess without gaps
            private static List<MultiCallBlockStateCallsModel> FillInMisingBlocks(MultiCallBlockStateCallsModel[] blockCallsToProcess)
            {
                var blockCalls = blockCallsToProcess.OrderBy(model => model.BlockOverride.Number).ToList();

                // Get the lowest and highest numbers
                var minNumber = blockCalls.First().BlockOverride.Number;
                var maxNumber = blockCalls.Last().BlockOverride.Number;

                // Generate a sequence of numbers in that range
                var rangeNumbers = Range(minNumber, maxNumber - minNumber + 1);

                // Get the list of current numbers
                var currentNumbers = new HashSet<UInt256>(blockCalls.Select(m => m.BlockOverride.Number));

                // Find which numbers are missing
                var missingNumbers = rangeNumbers.Where(n => !currentNumbers.Contains(n));

                // Fill in the gaps
                foreach (var missingNumber in missingNumbers)
                {
                    blockCalls.Add(new MultiCallBlockStateCallsModel()
                    {
                        BlockOverride = new BlockOverride() { Number = missingNumber }
                    });
                }

                // Sort again after filling the gaps
                blockCalls = blockCalls.OrderBy(model => model.BlockOverride.Number).ToList();
                return blockCalls;
            }
        }

        private class EstimateGasTxExecutor : TxExecutor<UInt256?>
        {
            public EstimateGasTxExecutor(IBlockchainBridge blockchainBridge, IBlockFinder blockFinder, IJsonRpcConfig rpcConfig)
                : base(blockchainBridge, blockFinder, rpcConfig)
            {
            }

            protected override ResultWrapper<UInt256?> ExecuteTx(BlockHeader header, Transaction tx, CancellationToken token)
            {
                BlockchainBridge.CallOutput result = _blockchainBridge.EstimateGas(header, tx, token);

                if (result.Error is null)
                {
                    return ResultWrapper<UInt256?>.Success((UInt256)result.GasSpent);
                }

                return result.InputError
                    ? GetInputError(result)
                    : ResultWrapper<UInt256?>.Fail(result.Error, ErrorCodes.ExecutionError);
            }
        }

        private class CreateAccessListTxExecutor : TxExecutor<AccessListForRpc>
        {
            private readonly bool _optimize;

            public CreateAccessListTxExecutor(IBlockchainBridge blockchainBridge, IBlockFinder blockFinder, IJsonRpcConfig rpcConfig, bool optimize)
                : base(blockchainBridge, blockFinder, rpcConfig)
            {
                _optimize = optimize;
            }

            protected override ResultWrapper<AccessListForRpc> ExecuteTx(BlockHeader header, Transaction tx, CancellationToken token)
            {
                BlockchainBridge.CallOutput result = _blockchainBridge.CreateAccessList(header, tx, token, _optimize);

                if (result.Error is null)
                {
                    return ResultWrapper<AccessListForRpc>.Success(new(GetResultAccessList(tx, result), GetResultGas(tx, result)));
                }

                return result.InputError
                    ? GetInputError(result)
                    : ResultWrapper<AccessListForRpc>.Fail(result.Error, ErrorCodes.ExecutionError, new(GetResultAccessList(tx, result), GetResultGas(tx, result)));
            }

            private static AccessListItemForRpc[] GetResultAccessList(Transaction tx, BlockchainBridge.CallOutput result)
            {
                AccessList? accessList = result.AccessList ?? tx.AccessList;
                return accessList is null ? Array.Empty<AccessListItemForRpc>() : AccessListItemForRpc.FromAccessList(accessList);
            }

            private static UInt256 GetResultGas(Transaction transaction, BlockchainBridge.CallOutput result)
            {
                long gas = result.GasSpent;
                if (result.AccessList is not null)
                {
                    // if we generated access list, we need to fix actual gas cost, as all storage was considered warm 
                    gas -= IntrinsicGasCalculator.Calculate(transaction, Berlin.Instance);
                    transaction.AccessList = result.AccessList;
                    gas += IntrinsicGasCalculator.Calculate(transaction, Berlin.Instance);
                }

                return (UInt256)gas;
            }
        }
    }
}
