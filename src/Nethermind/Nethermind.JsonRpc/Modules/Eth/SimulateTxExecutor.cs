// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Blockchain.Find;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Evm;
using Nethermind.Facade;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Facade.Simulate;

namespace Nethermind.JsonRpc.Modules.Eth;

public class SimulateTxExecutor<TTrace>(IBlockchainBridge blockchainBridge, IBlockFinder blockFinder, IJsonRpcConfig rpcConfig, ISimulateBlockTracerFactory<TTrace> simulateBlockTracerFactory, ulong? secondsPerSlot = null)
    : ExecutorBase<IReadOnlyList<SimulateBlockResult<TTrace>>, SimulatePayload<TransactionForRpc>,
    SimulatePayload<TransactionWithSourceDetails>>(blockchainBridge, blockFinder, rpcConfig)
{
    private readonly long _blocksLimit = rpcConfig.MaxSimulateBlocksCap ?? 256;
    private long _gasCapBudget = rpcConfig.GasCap ?? long.MaxValue;
    private readonly ulong _secondsPerSlot = secondsPerSlot ?? new BlocksConfig().SecondsPerSlot;

    protected override SimulatePayload<TransactionWithSourceDetails> Prepare(SimulatePayload<TransactionForRpc> call)
    {
        SimulatePayload<TransactionWithSourceDetails> result = new()
        {
            TraceTransfers = call.TraceTransfers,
            Validation = call.Validation,
            BlockStateCalls = call.BlockStateCalls?.Select(blockStateCall =>
            {
                if (blockStateCall.BlockOverrides?.GasLimit is not null)
                {
                    blockStateCall.BlockOverrides.GasLimit = (ulong)Math.Min((long)blockStateCall.BlockOverrides.GasLimit!.Value, _gasCapBudget);
                }

                return new BlockStateCall<TransactionWithSourceDetails>
                {
                    BlockOverrides = blockStateCall.BlockOverrides,
                    StateOverrides = blockStateCall.StateOverrides,
                    Calls = blockStateCall.Calls?.Select(callTransactionModel =>
                    {
                        callTransactionModel = UpdateTxType(callTransactionModel);
                        LegacyTransactionForRpc asLegacy = callTransactionModel as LegacyTransactionForRpc;
                        bool hadGasLimitInRequest = asLegacy?.Gas is not null;
                        bool hadNonceInRequest = asLegacy?.Nonce is not null;
                        asLegacy!.EnsureDefaults(_gasCapBudget);
                        _gasCapBudget -= asLegacy.Gas!.Value;
                        _gasCapBudget = Math.Max(0, _gasCapBudget);

                        Transaction tx = callTransactionModel.ToTransaction();
                        tx.ChainId = _blockchainBridge.GetChainId();

                        TransactionWithSourceDetails? result = new()
                        {
                            HadGasLimitInRequest = hadGasLimitInRequest,
                            HadNonceInRequest = hadNonceInRequest,
                            Transaction = tx
                        };

                        return result;
                    }).ToArray()
                };
            }).ToList()
        };

        return result;
    }

    private static TransactionForRpc UpdateTxType(TransactionForRpc rpcTransaction)
    {
        // TODO: This is a bit messy since we're changing the transaction type
        if (rpcTransaction is LegacyTransactionForRpc legacy)
        {
            rpcTransaction = new EIP1559TransactionForRpc
            {
                Nonce = legacy.Nonce,
                To = legacy.To,
                From = legacy.From,
                Gas = legacy.Gas,
                Value = legacy.Value,
                Input = legacy.Input,
                GasPrice = legacy.GasPrice,
                ChainId = legacy.ChainId,
                V = legacy.V,
                R = legacy.R,
                S = legacy.S,
            };
        }

        return rpcTransaction;
    }

    public override ResultWrapper<IReadOnlyList<SimulateBlockResult<TTrace>>> Execute(
        SimulatePayload<TransactionForRpc> call,
        BlockParameter? blockParameter,
        Dictionary<Address, AccountOverride>? stateOverride = null,
        SearchResult<BlockHeader>? searchResult = null)
    {
        if (call.BlockStateCalls is null)
            return ResultWrapper<IReadOnlyList<SimulateBlockResult<TTrace>>>.Fail("Must contain BlockStateCalls", ErrorCodes.InvalidParams);

        if (call.BlockStateCalls!.Count > _rpcConfig.MaxSimulateBlocksCap)
            return ResultWrapper<IReadOnlyList<SimulateBlockResult<TTrace>>>.Fail(
                $"This node is configured to support only {_rpcConfig.MaxSimulateBlocksCap} blocks", ErrorCodes.InvalidInputTooManyBlocks);

        searchResult ??= _blockFinder.SearchForHeader(blockParameter);

        if (searchResult.Value.IsError || searchResult.Value.Object is null)
            return ResultWrapper<IReadOnlyList<SimulateBlockResult<TTrace>>>.Fail(searchResult.Value);

        BlockHeader header = searchResult.Value.Object;

        if (!_blockchainBridge.HasStateForBlock(header!))
            return ResultWrapper<IReadOnlyList<SimulateBlockResult<TTrace>>>.Fail($"No state available for block {header.Hash}",
                ErrorCodes.ResourceUnavailable);

        if (call.BlockStateCalls?.Count > _blocksLimit)
            return ResultWrapper<IReadOnlyList<SimulateBlockResult<TTrace>>>.Fail(
                $"Too many blocks provided, node is configured to simulate up to {_blocksLimit} while {call.BlockStateCalls?.Count} were given",
                ErrorCodes.InvalidParams);

        if (call.BlockStateCalls is not null)
        {
            long lastBlockNumber = header.Number;
            ulong lastBlockTime = header.Timestamp;

            using ArrayPoolList<BlockStateCall<TransactionForRpc>> completeBlockStateCalls = new(call.BlockStateCalls.Count);

            foreach (BlockStateCall<TransactionForRpc>? blockToSimulate in call.BlockStateCalls)
            {
                blockToSimulate.BlockOverrides ??= new BlockOverride();
                ulong givenNumber = blockToSimulate.BlockOverrides.Number ?? (ulong)lastBlockNumber + 1;

                if (givenNumber > long.MaxValue)
                    return ResultWrapper<IReadOnlyList<SimulateBlockResult<TTrace>>>.Fail(
                        $"Block number too big {givenNumber}!", ErrorCodes.InvalidParams);

                if (givenNumber <= (ulong)lastBlockNumber)
                    return ResultWrapper<IReadOnlyList<SimulateBlockResult<TTrace>>>.Fail(
                        $"Block number out of order {givenNumber} is <= than previous block number of {header.Number}!", ErrorCodes.InvalidInputBlocksOutOfOrder);

                // if the no. of filler blocks are greater than maximum simulate blocks cap
                if (givenNumber - (ulong)lastBlockNumber > (ulong)_blocksLimit)
                    return ResultWrapper<IReadOnlyList<SimulateBlockResult<TTrace>>>.Fail(
                        $"too many blocks",
                        ErrorCodes.ClientLimitExceededError);

                for (ulong fillBlockNumber = (ulong)lastBlockNumber + 1; fillBlockNumber < givenNumber; fillBlockNumber++)
                {
                    ulong fillBlockTime = lastBlockTime + _secondsPerSlot;
                    completeBlockStateCalls.Add(new BlockStateCall<TransactionForRpc>
                    {
                        BlockOverrides = new BlockOverride { Number = fillBlockNumber, Time = fillBlockTime },
                        StateOverrides = null,
                        Calls = []
                    });
                    lastBlockTime = fillBlockTime;
                }

                blockToSimulate.BlockOverrides.Number = givenNumber;

                if (blockToSimulate.BlockOverrides.Time is not null)
                {
                    if (blockToSimulate.BlockOverrides.Time <= lastBlockTime)
                    {
                        return ResultWrapper<IReadOnlyList<SimulateBlockResult<TTrace>>>.Fail(
                            $"Block timestamp out of order {blockToSimulate.BlockOverrides.Time} is <= than given base timestamp of {lastBlockTime}!", ErrorCodes.BlockTimestampNotIncreased);
                    }
                    lastBlockTime = (ulong)blockToSimulate.BlockOverrides.Time;
                }
                else
                {
                    blockToSimulate.BlockOverrides.Time = lastBlockTime + _secondsPerSlot;
                    lastBlockTime = (ulong)blockToSimulate.BlockOverrides.Time;
                }
                lastBlockNumber = (long)givenNumber;

                completeBlockStateCalls.Add(blockToSimulate);
            }
            call.BlockStateCalls = [.. completeBlockStateCalls];
        }

        using CancellationTokenSource timeout = _rpcConfig.BuildTimeoutCancellationToken();
        SimulatePayload<TransactionWithSourceDetails> toProcess = Prepare(call);
        return Execute(header.Clone(), toProcess, stateOverride, timeout.Token);
    }

    protected override ResultWrapper<IReadOnlyList<SimulateBlockResult<TTrace>>> Execute(
        BlockHeader header,
        SimulatePayload<TransactionWithSourceDetails> tx,
        Dictionary<Address, AccountOverride>? stateOverride,
        CancellationToken token)
    {
        SimulateOutput<TTrace> results = _blockchainBridge.Simulate(header, tx, simulateBlockTracerFactory, token);

        foreach (SimulateBlockResult<TTrace> item in results.Items)
        {
            if (item is SimulateBlockResult<SimulateCallResult> result)
            {
                foreach (SimulateCallResult? call in result.Calls)
                {
                    if (call is { Error: not null } simulateResult && simulateResult.Error.Message != "")
                    {
                        simulateResult.Error.Code = ErrorCodes.ExecutionError;
                    }
                }
            }
        }

        if (results.Error is not null)
        {
            results.ErrorCode = results.Error switch
            {
                var x when x.Contains("invalid transaction") => ErrorCodes.InvalidTransaction,
                var x when x.Contains("InsufficientBalanceException") => ErrorCodes.InvalidTransaction,
                var x when x.Contains("InvalidBlockException") => ErrorCodes.InvalidParams,
                var x when x.Contains("below intrinsic gas") => ErrorCodes.InsufficientIntrinsicGas,
                _ => results.ErrorCode
            };
        }

        return results.Error is null
            ? ResultWrapper<IReadOnlyList<SimulateBlockResult<TTrace>>>.Success([.. results.Items])
            : results.ErrorCode is not null
                ? ResultWrapper<IReadOnlyList<SimulateBlockResult<TTrace>>>.Fail(results.Error!, results.ErrorCode!.Value, [.. results.Items])
                : ResultWrapper<IReadOnlyList<SimulateBlockResult<TTrace>>>.Fail(results.Error, [.. results.Items]);
    }
}
