// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Evm;
using Nethermind.Facade;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Int256;
using Nethermind.JsonRpc.Data;
using Nethermind.Specs.Forks;

namespace Nethermind.JsonRpc.Modules.Eth
{
    //General executor
    public partial class EthRpcModule
    {
        // Single call executor
        private abstract class SimulateCallExecutor<TResult>(
            IBlockchainBridge blockchainBridge,
            IBlockFinder blockFinder,
            IJsonRpcConfig rpcConfig,
            ulong? secondsPerSlot = null
        ) : SimulateTxExecutor(blockchainBridge, blockFinder, rpcConfig, secondsPerSlot)
        {
            private bool NoBaseFee { get; set; }

            protected override ResultWrapper<IReadOnlyList<SimulateBlockResult>> Execute(
                BlockHeader header, SimulatePayload<TransactionWithSourceDetails> payload,
                Dictionary<Address, AccountOverride>? stateOverride, CancellationToken token
            )
            {
                Transaction tx = GetSingleTransaction(payload);

                if (NoBaseFee)
                {
                    header.BaseFeePerGas = 0;
                }
                if (tx.IsContractCreation && tx.DataLength == 0)
                {
                    return ResultWrapper<IReadOnlyList<SimulateBlockResult>>.Fail(
                        "Contract creation without any data provided.", ErrorCodes.InvalidInput
                    );
                }

                return base.Execute(header, payload, stateOverride, token);
            }

            private static Transaction GetSingleTransaction(SimulatePayload<TransactionWithSourceDetails> payload) =>
                payload.BlockStateCalls!.Single().Calls!.Single().Transaction;

            private static SimulatePayload<TransactionForRpc> GetPayload(
                TransactionForRpc transactionCall, Dictionary<Address, AccountOverride>? stateOverride = null
            ) => new()
            {
                Validation = false,
                TraceTransfers = false,
                ReturnFullTransactionObjects = false,
                BlockStateCalls =
                [
                    new()
                    {
                        Calls = [transactionCall],
                        StateOverrides = stateOverride,
                        BlockOverrides = null
                    }
                ]
            };

            private static bool ShouldSetBaseFee(TransactionForRpc t) =>
                // x?.IsZero == false <=> x > 0
                t.GasPrice?.IsZero == false || t.MaxFeePerGas?.IsZero == false || t.MaxPriorityFeePerGas?.IsZero == false;

            public ResultWrapper<TResult> ExecuteCall(
                TransactionForRpc transactionCall,
                BlockParameter? blockParameter,
                Dictionary<Address, AccountOverride>? stateOverride = null)
            {
                NoBaseFee = !ShouldSetBaseFee(transactionCall);

                SimulatePayload<TransactionForRpc> payload = GetPayload(transactionCall, stateOverride);
                ResultWrapper<IReadOnlyList<SimulateBlockResult>> result = base.Execute(payload, blockParameter);

                if (result.Result.Error != null)
                    return ResultWrapper<TResult>.Fail(result.Result.Error, result.ErrorCode == 0 ? ErrorCodes.InvalidInput : result.ErrorCode);

                SimulateCallResult? simulateResult = result.Data?.SingleOrDefault()?.Calls.SingleOrDefault();

                if (simulateResult == null)
                    return ResultWrapper<TResult>.Fail("Internal error");

                return GetResult(simulateResult);
            }

            protected abstract ResultWrapper<TResult> GetResult(SimulateCallResult simulateResult);
        }

        private abstract class TxExecutor<TResult> : ExecutorBase<TResult, TransactionForRpc, Transaction>
        {
            private bool NoBaseFee { get; set; }

            protected TxExecutor(IBlockchainBridge blockchainBridge, IBlockFinder blockFinder, IJsonRpcConfig rpcConfig) : base(blockchainBridge, blockFinder, rpcConfig) { }

            protected override Transaction Prepare(TransactionForRpc call) => call.ToTransaction(_blockchainBridge.GetChainId());

            protected override ResultWrapper<TResult> Execute(BlockHeader header, Transaction tx, Dictionary<Address, AccountOverride>? stateOverride, CancellationToken token)
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
                return ExecuteTx(clonedHeader, tx, stateOverride, token);
            }

            private static bool ShouldSetBaseFee(TransactionForRpc t) =>
                // x?.IsZero == false <=> x > 0
                t.GasPrice?.IsZero == false || t.MaxFeePerGas?.IsZero == false || t.MaxPriorityFeePerGas?.IsZero == false;

            public override ResultWrapper<TResult> Execute(
                TransactionForRpc transactionCall,
                BlockParameter? blockParameter,
                Dictionary<Address, AccountOverride>? stateOverride = null)
            {
                NoBaseFee = !ShouldSetBaseFee(transactionCall);
                transactionCall.EnsureDefaults(_rpcConfig.GasCap);
                return base.Execute(transactionCall, blockParameter, stateOverride);
            }

            public ResultWrapper<TResult> ExecuteTx(TransactionForRpc transactionCall, BlockParameter? blockParameter, Dictionary<Address, AccountOverride>? stateOverride = null)
                => Execute(transactionCall, blockParameter, stateOverride);

            protected abstract ResultWrapper<TResult> ExecuteTx(BlockHeader header, Transaction tx, Dictionary<Address, AccountOverride>? stateOverride, CancellationToken token);

            protected ResultWrapper<TResult> GetInputError(CallOutput result) =>
                ResultWrapper<TResult>.Fail(result.Error, ErrorCodes.InvalidInput);
        }

        private class CallTxExecutor(IBlockchainBridge blockchainBridge, IBlockFinder blockFinder, IJsonRpcConfig rpcConfig, ulong? secondsPerSlot = null) : SimulateCallExecutor<string>(blockchainBridge, blockFinder, rpcConfig, secondsPerSlot)
        {
            protected override ResultWrapper<string> GetResult(SimulateCallResult simulateResult)
            {
                var data = simulateResult.ReturnData?.ToHexString(true);
                var error = simulateResult.Error;

                return error is null
                    ? ResultWrapper<string>.Success(data)
                    : ResultWrapper<string>.Fail(error.Data ?? "VM execution error.", error.Code, error.Message);
            }
        }

        private class EstimateGasTxExecutor(IBlockchainBridge blockchainBridge, IBlockFinder blockFinder, IJsonRpcConfig rpcConfig)
            : TxExecutor<UInt256?>(blockchainBridge, blockFinder, rpcConfig)
        {
            private readonly int _errorMargin = rpcConfig.EstimateErrorMargin;

            protected override ResultWrapper<UInt256?> ExecuteTx(BlockHeader header, Transaction tx, Dictionary<Address, AccountOverride> stateOverride, CancellationToken token)
            {
                CallOutput result = _blockchainBridge.EstimateGas(header, tx, _errorMargin, stateOverride, token);

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
            protected override ResultWrapper<AccessListForRpc?> ExecuteTx(BlockHeader header, Transaction tx, Dictionary<Address, AccountOverride> stateOverride, CancellationToken token)
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
