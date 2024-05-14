// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Blockchain.Find;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Facade;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Facade.Simulate;
using Nethermind.JsonRpc.Data;

namespace Nethermind.JsonRpc.Modules.Eth;

public class SimulateTxExecutor(IBlockchainBridge blockchainBridge, IBlockFinder blockFinder, IJsonRpcConfig rpcConfig, IBlocksConfig? blocksConfig = null)
    : ExecutorBase<IReadOnlyList<SimulateBlockResult>, SimulatePayload<TransactionForRpc>,
    SimulatePayload<TransactionWithSourceDetails>>(blockchainBridge, blockFinder, rpcConfig)
{
    private readonly ulong _interBlocksTimings = (blocksConfig ?? new BlocksConfig()).SecondsPerSlot;
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
                        if (callTransactionModel.Type == TxType.Legacy)
                        {
                            callTransactionModel.Type = TxType.EIP1559;
                        }

                        bool hadGasLimitInRequest = callTransactionModel.Gas.HasValue;
                        bool hadNonceInRequest = callTransactionModel.Nonce.HasValue;
                        callTransactionModel.EnsureDefaults(_gasCapBudget);
                        _gasCapBudget -= callTransactionModel.Gas!.Value;

                        Transaction tx = callTransactionModel.ToTransaction(_blockchainBridge.GetChainId());

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

    public override ResultWrapper<IReadOnlyList<SimulateBlockResult>> Execute(
        SimulatePayload<TransactionForRpc> call,
        BlockParameter? blockParameter)
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

        if (blockParameter?.Type == BlockParameterType.Latest) header = _blockFinder.FindLatestBlock()?.Header;

        if (!_blockchainBridge.HasStateForBlock(header!))
            return ResultWrapper<IReadOnlyList<SimulateBlockResult>>.Fail($"No state available for block {header.Hash}",
                ErrorCodes.ResourceUnavailable);

        if (call.BlockStateCalls?.Count > _blocksLimit)
            return ResultWrapper<IReadOnlyList<SimulateBlockResult>>.Fail(
                $"Too many blocks provided, node is configured to simulate up to {_blocksLimit} while {call.BlockStateCalls?.Count} were given",
                ErrorCodes.InvalidParams);

        if (call.BlockStateCalls is not null)
        {
            long lastBlockNumber = -1;
            ulong lastBlockTime = 0;

            foreach (BlockStateCall<TransactionForRpc>? blockToSimulate in call.BlockStateCalls)
            {
                ulong givenNumber = blockToSimulate.BlockOverrides?.Number ??
                                    (lastBlockNumber == -1 ? (ulong)header.Number + 1 : (ulong)lastBlockNumber + 1);

                if (givenNumber > long.MaxValue)
                    return ResultWrapper<IReadOnlyList<SimulateBlockResult>>.Fail(
                        $"Block number too big {givenNumber}!", ErrorCodes.InvalidParams);

                long given = (long)givenNumber;
                if (given > lastBlockNumber)
                {
                    lastBlockNumber = given;
                }
                else
                {
                    return ResultWrapper<IReadOnlyList<SimulateBlockResult>>.Fail(
                        $"Block number out of order {givenNumber}!", ErrorCodes.InvalidInputBlocksOutOfOrder);
                }

                blockToSimulate.BlockOverrides ??= new BlockOverride();
                blockToSimulate.BlockOverrides.Number = givenNumber;

                ulong givenTime = blockToSimulate.BlockOverrides.Time ??
                                  (lastBlockTime == 0
                                      ? header.Timestamp + _interBlocksTimings
                                      : lastBlockTime + _interBlocksTimings);

                if (givenTime > lastBlockTime)
                {
                    lastBlockTime = givenTime;
                }
                else
                {
                    return ResultWrapper<IReadOnlyList<SimulateBlockResult>>.Fail(
                        $"Block timestamp out of order {givenTime}!", ErrorCodes.InvalidInputBlocksOutOfOrder);
                }

                blockToSimulate.BlockOverrides.Time = givenTime;
            }

            long minBlockNumber = Math.Min(
                call.BlockStateCalls.Min(b => (long)(b.BlockOverrides?.Number ?? ulong.MaxValue)),
                header.Number + 1);

            long maxBlockNumber = Math.Max(
                call.BlockStateCalls.Max(b => (long)(b.BlockOverrides?.Number ?? ulong.MinValue)),
                minBlockNumber);

            HashSet<long> existingBlockNumbers =
            [
                ..call.BlockStateCalls.Select(b => (long)(b.BlockOverrides?.Number ?? ulong.MinValue))
            ];

            List<BlockStateCall<TransactionForRpc>> completeBlockStateCalls = call.BlockStateCalls;

            for (long blockNumber = minBlockNumber; blockNumber <= maxBlockNumber; blockNumber++)
            {
                if (!existingBlockNumbers.Contains(blockNumber))
                {
                    completeBlockStateCalls.Add(new BlockStateCall<TransactionForRpc>
                    {
                        BlockOverrides = new BlockOverride { Number = (ulong)blockNumber },
                        StateOverrides = null,
                        Calls = Array.Empty<TransactionForRpc>()
                    });
                }
            }

            call.BlockStateCalls.Sort((b1, b2) => b1.BlockOverrides!.Number!.Value.CompareTo(b2.BlockOverrides!.Number!.Value));
        }

        using CancellationTokenSource cancellationTokenSource = new(_rpcConfig.Timeout); //TODO remove!
        SimulatePayload<TransactionWithSourceDetails> toProcess = Prepare(call);
        return Execute(header.Clone(), toProcess, cancellationTokenSource.Token);
    }

    protected override ResultWrapper<IReadOnlyList<SimulateBlockResult>> Execute(BlockHeader header,
        SimulatePayload<TransactionWithSourceDetails> tx, CancellationToken token)
    {
        SimulateOutput results = _blockchainBridge.Simulate(header, tx, token);

        if (results.Error != null && (results.Error.Contains("invalid transaction")
                                      || results.Error.Contains("InsufficientBalanceException")))
            results.ErrorCode = ErrorCodes.InvalidTransaction;

        return results.Error is null
            ? ResultWrapper<IReadOnlyList<SimulateBlockResult>>.Success(results.Items)
            : results.ErrorCode is not null
                ? ResultWrapper<IReadOnlyList<SimulateBlockResult>>.Fail(results.Error!, results.ErrorCode!.Value, results.Items)
                : ResultWrapper<IReadOnlyList<SimulateBlockResult>>.Fail(results.Error, results.Items);
    }
}
