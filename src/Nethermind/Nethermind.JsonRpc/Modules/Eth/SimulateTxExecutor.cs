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

public class SimulateTxExecutor(IBlockchainBridge blockchainBridge, IBlockFinder blockFinder, IJsonRpcConfig rpcConfig)
    : ExecutorBase<IReadOnlyList<SimulateBlockResult>, SimulatePayload<TransactionForRpc>,
    SimulatePayload<TransactionWithSourceDetails>>(blockchainBridge, blockFinder, rpcConfig)
{
    private readonly long _blocksLimit = rpcConfig.MaxSimulateBlocksCap ?? 256;
    private long _gasCapBudget = rpcConfig.GasCap ?? long.MaxValue;

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

    public override ResultWrapper<IReadOnlyList<SimulateBlockResult>> Execute(
        SimulatePayload<TransactionForRpc> call,
        BlockParameter? blockParameter,
        Dictionary<Address, AccountOverride>? stateOverride = null)
    {
        if (call.BlockStateCalls is null)
            return ResultWrapper<IReadOnlyList<SimulateBlockResult>>.Fail("Must contain BlockStateCalls", ErrorCodes.InvalidParams);

        if (call.BlockStateCalls!.Count > _rpcConfig.MaxSimulateBlocksCap)
            return ResultWrapper<IReadOnlyList<SimulateBlockResult>>.Fail(
                $"This node is configured to support only {_rpcConfig.MaxSimulateBlocksCap} blocks", ErrorCodes.InvalidInputTooManyBlocks);

        SearchResult<Block> searchResult = _blockFinder.SearchForBlock(blockParameter);

        if (searchResult.IsError || searchResult.Object is null)
            return ResultWrapper<IReadOnlyList<SimulateBlockResult>>.Fail(searchResult);

        BlockHeader header = searchResult.Object.Header;

        if (!_blockchainBridge.HasStateForBlock(header!))
            return ResultWrapper<IReadOnlyList<SimulateBlockResult>>.Fail($"No state available for block {header.Hash}",
                ErrorCodes.ResourceUnavailable);

        if (call.BlockStateCalls?.Count > _blocksLimit)
            return ResultWrapper<IReadOnlyList<SimulateBlockResult>>.Fail(
                $"Too many blocks provided, node is configured to simulate up to {_blocksLimit} while {call.BlockStateCalls?.Count} were given",
                ErrorCodes.InvalidParams);

        ulong secondsPerSlot = new BlocksConfig().SecondsPerSlot;

        if (call.BlockStateCalls is not null)
        {
            long lastBlockNumber = header.Number;
            ulong lastBlockTime = header.Timestamp;

            using ArrayPoolList<BlockStateCall<TransactionForRpc>> completeBlockStateCalls = new(call.BlockStateCalls.Count, call.BlockStateCalls);

            foreach (BlockStateCall<TransactionForRpc>? blockToSimulate in call.BlockStateCalls)
            {
                blockToSimulate.BlockOverrides ??= new BlockOverride();
                ulong givenNumber = blockToSimulate.BlockOverrides.Number ?? (ulong)lastBlockNumber + 1;

                if (givenNumber > long.MaxValue)
                    return ResultWrapper<IReadOnlyList<SimulateBlockResult>>.Fail(
                        $"Block number too big {givenNumber}!", ErrorCodes.InvalidParams);

                if (givenNumber <= (ulong)lastBlockNumber)
                    return ResultWrapper<IReadOnlyList<SimulateBlockResult>>.Fail(
                        $"Block number out of order {givenNumber} is <= than previous block number of {header.Number}!", ErrorCodes.InvalidInputBlocksOutOfOrder);

                // if the no. of filler blocks are greater than maximum simulate blocks cap
                if (givenNumber - (ulong)lastBlockNumber > (ulong)_blocksLimit)
                    return ResultWrapper<IReadOnlyList<SimulateBlockResult>>.Fail(
                        $"too many blocks",
                        ErrorCodes.ClientLimitExceededError);

                for (ulong fillBlockNumber = (ulong)lastBlockNumber + 1; fillBlockNumber < givenNumber; fillBlockNumber++)
                {
                    ulong fillBlockTime = lastBlockTime + secondsPerSlot;
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
                        return ResultWrapper<IReadOnlyList<SimulateBlockResult>>.Fail(
                            $"Block timestamp out of order {blockToSimulate.BlockOverrides.Time} is <= than given base timestamp of {lastBlockTime}!", ErrorCodes.BlockTimestampNotIncreased);
                    }
                    lastBlockTime = (ulong)blockToSimulate.BlockOverrides.Time;
                }
                else
                {
                    blockToSimulate.BlockOverrides.Time = lastBlockTime + secondsPerSlot;
                    lastBlockTime = (ulong)blockToSimulate.BlockOverrides.Time;
                }
                lastBlockNumber = (long)givenNumber;
            }
            // sort element of the completeBlockStateCalls by block number
            completeBlockStateCalls.Sort((a, b) => a.BlockOverrides.Number!.Value.CompareTo(b.BlockOverrides.Number!.Value));
            call.BlockStateCalls = [.. completeBlockStateCalls];
        }

        using CancellationTokenSource cancellationTokenSource = new(_rpcConfig.Timeout); //TODO remove!
        SimulatePayload<TransactionWithSourceDetails> toProcess = Prepare(call);
        return Execute(header.Clone(), toProcess, stateOverride, cancellationTokenSource.Token);
    }

    protected override ResultWrapper<IReadOnlyList<SimulateBlockResult>> Execute(BlockHeader header,
        SimulatePayload<TransactionWithSourceDetails> tx, Dictionary<Address, AccountOverride>? stateOverride, CancellationToken token)
    {
        SimulateOutput results = _blockchainBridge.Simulate(header, tx, token);

        foreach (SimulateBlockResult result in results.Items)
        {
            foreach (SimulateCallResult? call in result.Calls)
            {
                if (call?.Error is not null && call.Error.Message != "")
                {
                    call.Error.Code = ErrorCodes.ExecutionError;
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
            ? ResultWrapper<IReadOnlyList<SimulateBlockResult>>.Success(results.Items)
            : results.ErrorCode is not null
                ? ResultWrapper<IReadOnlyList<SimulateBlockResult>>.Fail(results.Error!, results.ErrorCode!.Value, results.Items)
                : ResultWrapper<IReadOnlyList<SimulateBlockResult>>.Fail(results.Error, results.Items);
    }
}
