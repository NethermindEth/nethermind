// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Threading;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Extensions;
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
        private abstract class TxExecutor<TResult>(IBlockchainBridge blockchainBridge, IBlockFinder blockFinder, IJsonRpcConfig rpcConfig)
            : ExecutorBase<TResult, TransactionForRpc, Transaction>(blockchainBridge, blockFinder, rpcConfig)
        {
            private bool NoBaseFee { get; set; }

            protected override string? Validate(TransactionForRpc call)
            {
                if (call is LegacyTransactionForRpc legacyTransaction)
                {
                    if (legacyTransaction.To is null && legacyTransaction.Input is null or { Length: 0 })
                        return ContractCreationWithoutDataError;
                }

                if (call is EIP1559TransactionForRpc eip1559Transaction)
                {
                    if (eip1559Transaction.GasPrice != null)
                        return GasPriceInEip1559Error;

                    if (eip1559Transaction.MaxFeePerGas is not null && eip1559Transaction.MaxFeePerGas == 0)
                        return ZeroMaxFeePerGasError;

                    if (eip1559Transaction.MaxFeePerGas < eip1559Transaction.MaxPriorityFeePerGas)
                        return MaxFeePerGasSmallerThenMaxPriorityFeePerGasError(
                            eip1559Transaction.MaxFeePerGas,
                            eip1559Transaction.MaxPriorityFeePerGas);
                }

                if (call is BlobTransactionForRpc blobTransaction)
                {
                    if (blobTransaction.BlobVersionedHashes is null || blobTransaction.BlobVersionedHashes.Length == 0)
                        return AtLeastOneBlobInBlobTransactionError;

                    if (blobTransaction.To is null)
                        return MissingToInBlobTxError;

                    if (blobTransaction.MaxFeePerBlobGas is not null && blobTransaction.MaxFeePerBlobGas == 0)
                        return ZeroMaxFeePerBlobGasError;
                }

                return null;
            }

            protected override Transaction Prepare(TransactionForRpc call)
            {
                var tx = call.ToTransaction();
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
                    {
                        transactionCall.Gas = searchResult.Value.Object.GasLimit;
                    }
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

                        var errorData = errorMessage is not null ? Encoding.UTF8.GetBytes(errorMessage).ToHexString(true) : null;
                        return ResultWrapper<TResult, string?>.Fail("execution reverted: " + errorMessage, ErrorCodes.ExecutionReverted, errorData);
                    }

                    return ResultWrapper<TResult>.Fail(errorMessage, ErrorCodes.InvalidInput, bodyData);
                }

                return ResultWrapper<TResult>.Success(bodyData);
            }

            private const string GasPriceInEip1559Error = "both gasPrice and (maxFeePerGas or maxPriorityFeePerGas) specified";
            private const string AtLeastOneBlobInBlobTransactionError = "need at least 1 blob for a blob transaction";
            private const string MissingToInBlobTxError = "missing \"to\" in blob transaction";
            private const string ZeroMaxFeePerBlobGasError = "maxFeePerBlobGas, if specified, must be non-zero";
            private const string ZeroMaxFeePerGasError = "maxFeePerGas must be non-zero";
            private static string MaxFeePerGasSmallerThenMaxPriorityFeePerGasError(
                UInt256? maxFeePerGas,
                UInt256? maxPriorityFeePerGas)
                => $"maxFeePerGas ({maxFeePerGas}) < maxPriorityFeePerGas ({maxPriorityFeePerGas})";

            private const string ContractCreationWithoutDataError = "contract creation without any data provided";
        }

        private class CallTxExecutor(IBlockchainBridge blockchainBridge, IBlockFinder blockFinder, IJsonRpcConfig rpcConfig)
            : TxExecutor<string>(blockchainBridge, blockFinder, rpcConfig)
        {
            protected override ResultWrapper<string> ExecuteTx(BlockHeader header, Transaction tx, Dictionary<Address, AccountOverride>? stateOverride, CancellationToken token)
            {
                CallOutput result = _blockchainBridge.Call(header, tx, stateOverride, token);

                return CreateResultWrapper(result.InputError, result.Error, result.OutputData?.ToHexString(true), result.ExecutionReverted, result.OutputData);
            }
        }

        private class EstimateGasTxExecutor(IBlockchainBridge blockchainBridge, IBlockFinder blockFinder, IJsonRpcConfig rpcConfig)
            : TxExecutor<UInt256?>(blockchainBridge, blockFinder, rpcConfig)
        {
            private readonly int _errorMargin = rpcConfig.EstimateErrorMargin;

            protected override ResultWrapper<UInt256?> ExecuteTx(BlockHeader header, Transaction tx, Dictionary<Address, AccountOverride> stateOverride, CancellationToken token)
            {
                CallOutput result = _blockchainBridge.EstimateGas(header, tx, _errorMargin, stateOverride, token);

                return CreateResultWrapper(result.InputError, result.Error, result.InputError || result.Error is not null ? null : (UInt256)result.GasSpent, result.ExecutionReverted, result.OutputData);
            }
        }

        private class CreateAccessListTxExecutor(IBlockchainBridge blockchainBridge, IBlockFinder blockFinder, IJsonRpcConfig rpcConfig, bool optimize)
            : TxExecutor<AccessListResultForRpc?>(blockchainBridge, blockFinder, rpcConfig)
        {
            protected override ResultWrapper<AccessListResultForRpc?> ExecuteTx(BlockHeader header, Transaction tx, Dictionary<Address, AccountOverride> stateOverride, CancellationToken token)
            {
                CallOutput result = _blockchainBridge.CreateAccessList(header, tx, token, optimize);

                var rpcAccessListResult = new AccessListResultForRpc(
                    accessList: AccessListForRpc.FromAccessList(result.AccessList ?? tx.AccessList),
                    gasUsed: GetResultGas(tx, result));

                return CreateResultWrapper(result.InputError, result.Error, rpcAccessListResult, false, null);
            }

            private static UInt256 GetResultGas(Transaction transaction, CallOutput result)
            {
                long gas = result.GasSpent;
                long operationGas = result.OperationGas;
                if (result.AccessList is not null)
                {
                    var oldIntrinsicCost = IntrinsicGasCalculator.AccessListCost(transaction, Berlin.Instance);
                    transaction.AccessList = result.AccessList;
                    var newIntrinsicCost = IntrinsicGasCalculator.AccessListCost(transaction, Berlin.Instance);
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
