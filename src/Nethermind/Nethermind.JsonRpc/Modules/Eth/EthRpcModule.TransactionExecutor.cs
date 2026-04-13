// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text;
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
using Nethermind.Specs.Forks;

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

            protected override Result<Transaction> Prepare(TransactionForRpc call, BlockHeader header)
            {
                IReleaseSpec spec = specProvider.GetSpec(header);
                Result<Transaction> result = call.ToTransaction(validateUserInput: true, spec: spec);
                if (result.IsError) return result;

                Transaction tx = result.Data;
                tx.ChainId = _blockchainBridge.GetChainId();
                return tx;
            }

            protected override ResultWrapper<TResult> Execute(BlockHeader header, Transaction tx, Dictionary<Address, AccountOverride>? stateOverride, CancellationToken token)
            {
                BlockHeader clonedHeader = header.Clone();

                if (NoBaseFee)
                {
                    clonedHeader.BaseFeePerGas = 0;
                }

                clonedHeader.GasUsed = 0;

                return ExecuteTx(clonedHeader, tx, stateOverride, token);
            }

            public override ResultWrapper<TResult> Execute(
                TransactionForRpc transactionCall,
                BlockParameter? blockParameter,
                Dictionary<Address, AccountOverride>? stateOverride = null,
                SearchResult<BlockHeader>? searchResult = null)
            {
                NoBaseFee = !transactionCall.ShouldSetBaseFee();

                // default to previous block gas if unspecified
                if (transactionCall.Gas is null)
                {
                    searchResult ??= _blockFinder.SearchForHeader(blockParameter);
                    if (!searchResult.Value.IsError)
                        transactionCall.Gas = searchResult.Value.Object!.GasLimit;
                }

                // enforces gas cap
                transactionCall.EnsureDefaults(_rpcConfig.GasCap);

                return base.Execute(transactionCall, blockParameter, stateOverride, searchResult);
            }

            public ResultWrapper<TResult> ExecuteTx(TransactionForRpc transactionCall, BlockParameter? blockParameter, Dictionary<Address, AccountOverride>? stateOverride = null)
                => Execute(transactionCall, blockParameter, stateOverride);

            protected abstract ResultWrapper<TResult> ExecuteTx(BlockHeader header, Transaction tx, Dictionary<Address, AccountOverride>? stateOverride, CancellationToken token);

            protected ResultWrapper<TResult> CreateResultWrapper(bool inputError, string? errorMessage, TResult? bodyData, bool executionReverted, byte[]? executionRevertedReason)
            {
                if (inputError || errorMessage is not null)
                {
                    if (executionReverted)
                    {
                        if (executionRevertedReason is not null)
                        {
                            return ResultWrapper<TResult, string>.Fail("execution reverted: " + errorMessage, ErrorCodes.ExecutionReverted, executionRevertedReason.ToHexString(true));
                        }

                        string? errorData = errorMessage is not null ? Encoding.UTF8.GetBytes(errorMessage).ToHexString(true) : null;
                        return ResultWrapper<TResult, string?>.Fail("execution reverted: " + errorMessage, ErrorCodes.ExecutionReverted, errorData);
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
                CallOutput result = _blockchainBridge.Call(header, tx, stateOverride, token);
                return CreateResultWrapper(result.InputError, result.Error, result.OutputData?.ToHexString(true), result.ExecutionReverted, result.OutputData);
            }
        }

        private class EstimateGasTxExecutor(IBlockchainBridge blockchainBridge, IBlockFinder blockFinder, IJsonRpcConfig rpcConfig, ISpecProvider specProvider)
            : TxExecutor<UInt256?>(blockchainBridge, blockFinder, rpcConfig, specProvider)
        {
            private readonly int _errorMargin = rpcConfig.EstimateErrorMargin;

            protected override ResultWrapper<UInt256?> ExecuteTx(BlockHeader header, Transaction tx, Dictionary<Address, AccountOverride> stateOverride, CancellationToken token)
            {
                CallOutput result = _blockchainBridge.EstimateGas(header, tx, _errorMargin, stateOverride, token);

                return CreateResultWrapper(result.InputError, result.Error, result.InputError || result.Error is not null ? null : (UInt256)result.GasSpent, result.ExecutionReverted, result.OutputData);
            }
        }

        private class CreateAccessListTxExecutor(IBlockchainBridge blockchainBridge, IBlockFinder blockFinder, IJsonRpcConfig rpcConfig, ISpecProvider specProvider, bool optimize)
            : TxExecutor<AccessListResultForRpc?>(blockchainBridge, blockFinder, rpcConfig, specProvider)
        {
            protected override ResultWrapper<AccessListResultForRpc?> ExecuteTx(BlockHeader header, Transaction tx, Dictionary<Address, AccountOverride> stateOverride, CancellationToken token)
            {
                CallOutput result = _blockchainBridge.CreateAccessList(header, tx, stateOverride, token, optimize);

                AccessListResultForRpc rpcAccessListResult = new(
                    accessList: AccessListForRpc.FromAccessList(result.AccessList ?? tx.AccessList),
                    gasUsed: GetResultGas(tx, result),
                    result.Error);

                return result.InputError
                    ? ResultWrapper<AccessListResultForRpc?>.Fail(result.Error!, ErrorCodes.InvalidInput)
                    : ResultWrapper<AccessListResultForRpc?>.Success(rpcAccessListResult);
            }

            private static UInt256 GetResultGas(Transaction transaction, CallOutput result)
            {
                long gas = result.GasSpent;
                long operationGas = result.OperationGas;
                if (result.AccessList is not null)
                {
                    long oldIntrinsicCost = IntrinsicGasCalculator.AccessListCost(transaction, Berlin.Instance);
                    transaction.AccessList = result.AccessList;
                    long newIntrinsicCost = IntrinsicGasCalculator.AccessListCost(transaction, Berlin.Instance);
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
