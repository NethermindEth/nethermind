// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Xml;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Evm;
using Nethermind.Facade;
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

            protected override Transaction Prepare(TransactionForRpc call) => call.ToTransaction(_blockchainBridge.GetChainId());

            protected override ResultWrapper<TResult> Execute(BlockHeader header, Transaction tx, CancellationToken token)
            {
                BlockHeader clonedHeader = header.Clone();
                if (NoBaseFee)
                {
                    clonedHeader.BaseFeePerGas = 0;
                }
                if (tx.IsContractCreation && tx.DataLength == 0)
                {
                    return ResultWrapper<TResult>.Fail("Contract creation without any data provided.", ErrorCodes.InvalidInput);
                }
                return ExecuteTx(clonedHeader, tx, token);
            }

            private static bool ShouldSetBaseFee(TransactionForRpc t) =>
                // x?.IsZero == false <=> x > 0
                t.GasPrice?.IsZero == false || t.MaxFeePerGas?.IsZero == false || t.MaxPriorityFeePerGas?.IsZero == false;

            public override ResultWrapper<TResult> Execute(
                TransactionForRpc transactionCall,
                BlockParameter? blockParameter)
            {
                NoBaseFee = !ShouldSetBaseFee(transactionCall);
                transactionCall.EnsureDefaults(_rpcConfig.GasCap);
                return base.Execute(transactionCall, blockParameter);
            }

            public ResultWrapper<TResult> ExecuteTx(TransactionForRpc transactionCall, BlockParameter? blockParameter) => Execute(transactionCall, blockParameter);

            protected abstract ResultWrapper<TResult> ExecuteTx(BlockHeader header, Transaction tx, CancellationToken token);

            protected ResultWrapper<TResult> GetInputError(CallOutput result) =>
                ResultWrapper<TResult>.Fail(result.Error, ErrorCodes.InvalidInput);
        }

        private class CallTxExecutor(IBlockchainBridge blockchainBridge, IBlockFinder blockFinder, IJsonRpcConfig rpcConfig)
            : TxExecutor<string>(blockchainBridge, blockFinder, rpcConfig)
        {
            protected override ResultWrapper<string> ExecuteTx(BlockHeader header, Transaction tx, CancellationToken token)
            {
                CallOutput result = _blockchainBridge.Call(header, tx, token);

                return result.Error is null
                    ? ResultWrapper<string>.Success(result.OutputData.ToHexString(true))
                    : TryGetInputError(result) ?? ResultWrapper<string>.Fail("VM execution error.", ErrorCodes.ExecutionError, result.Error);
            }

        }

        private class EstimateGasTxExecutor(IBlockchainBridge blockchainBridge, IBlockFinder blockFinder, IJsonRpcConfig rpcConfig)
            : TxExecutor<UInt256?>(blockchainBridge, blockFinder, rpcConfig)
        {
            private readonly int _errorMargin = rpcConfig.EstimateErrorMargin;

            protected override ResultWrapper<UInt256?> ExecuteTx(BlockHeader header, Transaction tx, CancellationToken token)
            {
                CallOutput result = _blockchainBridge.EstimateGas(header, tx, _errorMargin, token);

                if (result.Error is null)
                {
                    return ResultWrapper<UInt256?>.Success((UInt256)result.GasSpent);
                }

                return result.InputError
                    ? GetInputError(result)
                    : ResultWrapper<UInt256?>.Fail(result.Error, ErrorCodes.ExecutionError);
            }
        }

        private class CreateAccessListTxExecutor(IBlockchainBridge blockchainBridge, IBlockFinder blockFinder, IJsonRpcConfig rpcConfig, bool optimize)
            : TxExecutor<AccessListForRpc?>(blockchainBridge, blockFinder, rpcConfig)
        {
            protected override ResultWrapper<AccessListForRpc?> ExecuteTx(BlockHeader header, Transaction tx, CancellationToken token)
            {
                CallOutput result = _blockchainBridge.CreateAccessList(header, tx, token, optimize);

                if (result.Error is null)
                {
                    return ResultWrapper<AccessListForRpc?>.Success(new(GetResultAccessList(tx, result), GetResultGas(tx, result)));
                }

                return result.InputError
                    ? GetInputError(result)
                    : ResultWrapper<AccessListForRpc?>.Fail(result.Error, ErrorCodes.ExecutionError, new AccessListForRpc(GetResultAccessList(tx, result), GetResultGas(tx, result)));
            }

            private static IEnumerable<AccessListItemForRpc> GetResultAccessList(Transaction tx, CallOutput result)
            {
                AccessList? accessList = result.AccessList ?? tx.AccessList;
                return accessList is null ? Enumerable.Empty<AccessListItemForRpc>() : AccessListItemForRpc.FromAccessList(accessList);
            }

            private static UInt256 GetResultGas(Transaction transaction, CallOutput result)
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
