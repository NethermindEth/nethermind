// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

            protected override Transaction Prepare(TransactionForRpc call)
            {
                var tx = call.ToTransaction();
                tx.ChainId = _blockchainBridge.GetChainId();
                return tx;
            }

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

            // TODO: Should we move this method to `RpcNethermindTransaction` directly?
            private static bool ShouldSetBaseFee(TransactionForRpc t)
            {
                var positiveGasPrice = false;
                if (t is LegacyTransactionForRpc legacy)
                {
                    positiveGasPrice = IsPositive(legacy.GasPrice);
                }

                var positiveMaxFeePerGas = false;
                var positiveMaxPriorityFeePerGas = false;
                if (t is EIP1559TransactionForRpc eip1559)
                {
                    positiveMaxFeePerGas = IsPositive(eip1559.MaxFeePerGas);
                    positiveMaxPriorityFeePerGas = IsPositive(eip1559.MaxPriorityFeePerGas);
                }

                return positiveGasPrice || positiveMaxFeePerGas || positiveMaxPriorityFeePerGas;

                // value?.IsZero == false <=> x > 0
                static bool IsPositive(UInt256? value) => value?.IsZero == false;
            }

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

                return result switch
                {
                    { Error: null } => ResultWrapper<UInt256?>.Success((UInt256)result.GasSpent),
                    { InputError: true } => ResultWrapper<UInt256?>.Fail(result.Error, ErrorCodes.InvalidInput),
                    _ => ResultWrapper<UInt256?>.Fail(result.Error, ErrorCodes.ExecutionError)
                };
            }
        }

        private class CreateAccessListTxExecutor(IBlockchainBridge blockchainBridge, IBlockFinder blockFinder, IJsonRpcConfig rpcConfig, bool optimize)
            : TxExecutor<AccessListResultForRpc?>(blockchainBridge, blockFinder, rpcConfig)
        {
            protected override ResultWrapper<AccessListResultForRpc?> ExecuteTx(BlockHeader header, Transaction tx, CancellationToken token)
            {
                CallOutput result = _blockchainBridge.CreateAccessList(header, tx, token, optimize);

                var rpcAccessListResult = new AccessListResultForRpc(
                    accessList: AccessListForRpc.FromAccessList(result.AccessList ?? tx.AccessList),
                    gasUsed: GetResultGas(tx, result));

                return result switch
                {
                    { Error: null } => ResultWrapper<AccessListResultForRpc?>.Success(rpcAccessListResult),
                    { InputError: true } => ResultWrapper<AccessListResultForRpc?>.Fail(result.Error, ErrorCodes.InvalidInput),
                    _ => ResultWrapper<AccessListResultForRpc?>.Fail(result.Error, ErrorCodes.ExecutionError),
                };
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
