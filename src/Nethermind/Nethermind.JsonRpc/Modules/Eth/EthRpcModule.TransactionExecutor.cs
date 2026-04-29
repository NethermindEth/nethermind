// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Facade;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.JsonRpc.Data;

namespace Nethermind.JsonRpc.Modules.Eth
{
    //General executor
    public partial class EthRpcModule
    {
        // Single call executor
        private abstract class TxExecutor<TResult>(IBlockchainBridge blockchainBridge, IBlockFinder blockFinder, IJsonRpcConfig rpcConfig, ISpecProvider specProvider)
            : ExecutorBase<TResult, TransactionForRpc, Transaction>(blockchainBridge, blockFinder, rpcConfig)
        {
            private bool NoBaseFee { get; set; }
            private BlockOverride? _blockOverride;
            protected BlockOverride? BlockOverride => _blockOverride;
            protected UInt256? BlobBaseFeeOverride => _blockOverride?.BlobBaseFee;

            protected IReleaseSpec GetSpec(BlockHeader header) => specProvider.GetSpec(header);

            protected override Result<Transaction> Prepare(TransactionForRpc call, BlockHeader header)
            {
                IReleaseSpec spec = GetSpec(header);
                Result<Transaction> result = call.ToTransaction(validateUserInput: true, spec: spec);
                if (result.IsError) return result;

                Transaction tx = result.Data;
                tx.ChainId = _blockchainBridge.GetChainId();
                return tx;
            }

            protected override ResultWrapper<TResult> Execute(BlockHeader header, Transaction tx, Dictionary<Address, AccountOverride>? stateOverride, CancellationToken token)
            {
                if (stateOverride is not null)
                {
                    IReleaseSpec spec = specProvider.GetSpec(header);
                    foreach ((Address address, AccountOverride accountOverride) in stateOverride)
                    {
                        if (accountOverride.MovePrecompileToAddress is not null &&
                            spec.IsPrecompile(address) &&
                            accountOverride.MovePrecompileToAddress == address)
                        {
                            return ResultWrapper<TResult>.Fail(
                                $"account {address} is already overridden",
                                ErrorCodes.InvalidInput);
                        }
                    }
                }

                BlockHeader clonedHeader = header.Clone();

                if (NoBaseFee)
                {
                    clonedHeader.BaseFeePerGas = 0;
                }

                clonedHeader.GasUsed = 0;
                _blockOverride?.ApplyOverrides(clonedHeader);

                return ExecuteTx(clonedHeader, tx, stateOverride, token);
            }

            public override ResultWrapper<TResult> Execute(
                TransactionForRpc transactionCall,
                BlockParameter? blockParameter,
                Dictionary<Address, AccountOverride>? stateOverride = null,
                SearchResult<BlockHeader>? searchResult = null)
            {

                // enforces gas cap
                NoBaseFee = !transactionCall.ShouldSetBaseFee();

                transactionCall.EnsureDefaults(_rpcConfig.GasCap);

                return base.Execute(transactionCall, blockParameter, stateOverride, searchResult);
            }

            public ResultWrapper<TResult> ExecuteTx(TransactionForRpc transactionCall, BlockParameter? blockParameter, Dictionary<Address, AccountOverride>? stateOverride = null, BlockOverride? blockOverride = null)
            {
                if (blockOverride?.GasLimit > (ulong)_rpcConfig.GasCap!.Value)
                    return ResultWrapper<TResult>.Fail($"GasLimit value is too large, max value {_rpcConfig.GasCap.Value}", ErrorCodes.InvalidInput);
                _blockOverride = blockOverride;
                return Execute(transactionCall, blockParameter, stateOverride);
            }

            protected abstract ResultWrapper<TResult> ExecuteTx(BlockHeader header, Transaction tx, Dictionary<Address, AccountOverride>? stateOverride, CancellationToken token);

            protected ResultWrapper<TResult> CreateResultWrapper(bool inputError, string? errorMessage, TResult? bodyData, bool executionReverted, byte[]? executionRevertedReason)
            {
                if (inputError || errorMessage is not null)
                {
                    if (executionReverted)
                    {
                        // Check the raw revert bytes to determine if the selector is Error(string) or Panic(uint256).
                        // Using the bytes rather than errorMessage avoids the sentinel-string collision
                        // (e.g. require(false, "revert") would be misidentified as the Revert sentinel).
                        bool isKnownRevertType = executionRevertedReason is { Length: >= 4 } &&
                            (executionRevertedReason.AsSpan(0, 4).SequenceEqual(TransactionSubstate.ErrorFunctionSelector) ||
                             executionRevertedReason.AsSpan(0, 4).SequenceEqual(TransactionSubstate.PanicFunctionSelector));

                        string revertMessage = isKnownRevertType && errorMessage is not null
                            ? "execution reverted: " + errorMessage
                            : "execution reverted";

                        if (executionRevertedReason is not null)
                        {
                            return ResultWrapper<TResult, string>.Fail(revertMessage, ErrorCodes.ExecutionReverted, executionRevertedReason.ToHexString(true));
                        }

                        return ResultWrapper<TResult, string?>.Fail(revertMessage, ErrorCodes.ExecutionReverted, null);
                    }

                    return ResultWrapper<TResult>.Fail(errorMessage ?? "", ErrorCodes.InvalidInput, bodyData);
                }

                return ResultWrapper<TResult>.Success(bodyData);
            }
        }

        private class CallTxExecutor(IBlockchainBridge blockchainBridge, IBlockFinder blockFinder, IJsonRpcConfig rpcConfig, ISpecProvider specProvider)
            : TxExecutor<string>(blockchainBridge, blockFinder, rpcConfig, specProvider)
        {
            protected override ResultWrapper<string> ExecuteTx(BlockHeader header, Transaction tx, Dictionary<Address, AccountOverride>? stateOverride, CancellationToken token)
            {
                CallOutput result = _blockchainBridge.Call(header, tx, stateOverride, BlobBaseFeeOverride, token);

                if (!result.ExecutionReverted && result.Error is not null)
                {
                    string message = result.InputError
                        ? ErrorWrapper.EthCall(result.Error, tx.GasLimit)
                        : result.Error;
                    return ResultWrapper<string>.Fail(message, ErrorCodes.ExecutionError);
                }

                return CreateResultWrapper(result.InputError, result.Error, result.OutputData?.ToHexString(true), result.ExecutionReverted, result.OutputData);
            }
        }

        private class EstimateGasTxExecutor(IBlockchainBridge blockchainBridge, IBlockFinder blockFinder, IJsonRpcConfig rpcConfig, ISpecProvider specProvider)
            : TxExecutor<UInt256?>(blockchainBridge, blockFinder, rpcConfig, specProvider)
        {
            private readonly int _errorMargin = rpcConfig.EstimateErrorMargin;

            public override ResultWrapper<UInt256?> Execute(
                TransactionForRpc transactionCall,
                BlockParameter? blockParameter,
                Dictionary<Address, AccountOverride>? stateOverride = null,
                SearchResult<BlockHeader>? searchResult = null)
            {
                // Match Geth: when no gas is specified, binary search is bounded by blockGasLimit (then
                // capped at gasCap by EnsureDefaults). eth_call uses gasCap directly because it is a
                // pure simulation; estimateGas is computing gas for a real transaction that must fit in a block.
                if (transactionCall.Gas is null)
                {
                    if (BlockOverride?.GasLimit is not null)
                    {
                        transactionCall.Gas = (long)BlockOverride.GasLimit.Value;
                    }
                    else
                    {
                        searchResult ??= _blockFinder.SearchForHeader(blockParameter);
                        if (!searchResult.Value.IsError)
                            transactionCall.Gas = searchResult.Value.Object!.GasLimit;
                    }
                }
                return base.Execute(transactionCall, blockParameter, stateOverride, searchResult);
            }

            protected override ResultWrapper<UInt256?> ExecuteTx(BlockHeader header, Transaction tx, Dictionary<Address, AccountOverride> stateOverride, CancellationToken token)
            {
                CallOutput result = _blockchainBridge.EstimateGas(header, tx, _errorMargin, stateOverride, BlobBaseFeeOverride, token);

                string? errorMessage = result.Error;
                if (!result.ExecutionReverted && !result.InputError && errorMessage is not null)
                {
                    errorMessage = ErrorWrapper.EstimateGasBinarySearch(errorMessage, tx.GasLimit);
                }

                return CreateResultWrapper(result.InputError, errorMessage, result.InputError || result.Error is not null ? null : (UInt256)result.GasSpent, result.ExecutionReverted, result.OutputData);
            }
        }

        private class CreateAccessListTxExecutor(IBlockchainBridge blockchainBridge, IBlockFinder blockFinder, IJsonRpcConfig rpcConfig, ISpecProvider specProvider, bool optimize)
            : TxExecutor<AccessListResultForRpc?>(blockchainBridge, blockFinder, rpcConfig, specProvider)
        {
            protected override ResultWrapper<AccessListResultForRpc?> ExecuteTx(BlockHeader header, Transaction tx, Dictionary<Address, AccountOverride> stateOverride, CancellationToken token)
            {
                CallOutput result = _blockchainBridge.CreateAccessList(header, tx, stateOverride, optimize, BlobBaseFeeOverride, token);
                IReleaseSpec spec = GetSpec(header);

                AccessListResultForRpc rpcAccessListResult = new(
                    accessList: AccessListForRpc.FromAccessList(result.AccessList ?? tx.AccessList),
                    gasUsed: GetResultGas(tx, result, spec),
                    result.Error);

                if (result.InputError)
                {
                    string wrapped = ErrorWrapper.CreateAccessList(result.Error!, tx.Hash!);
                    return ResultWrapper<AccessListResultForRpc?>.Fail(wrapped, ErrorCodes.InvalidInput);
                }
                return ResultWrapper<AccessListResultForRpc?>.Success(rpcAccessListResult);
            }

            private static UInt256 GetResultGas(Transaction transaction, CallOutput result, IReleaseSpec spec)
            {
                long gas = result.GasSpent;
                long operationGas = result.OperationGas;
                if (result.AccessList is not null)
                {
                    long oldIntrinsicCost = IntrinsicGasCalculator.AccessListCost(transaction, spec);
                    transaction.AccessList = result.AccessList;
                    long newIntrinsicCost = IntrinsicGasCalculator.AccessListCost(transaction, spec);
                    long updatedAccessListCost = newIntrinsicCost - oldIntrinsicCost;
                    if (gas > operationGas)
                    {
                        if (gas - operationGas < updatedAccessListCost) gas = operationGas + updatedAccessListCost;
                    }
                    else
                    {
                        gas += updatedAccessListCost;
                    }
                }

                return (UInt256)gas;
            }
        }
    }
}
