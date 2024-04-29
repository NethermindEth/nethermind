// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Facade;
using Nethermind.Facade.Simulate;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.JsonRpc.Data;
using Newtonsoft.Json.Linq;

namespace Nethermind.JsonRpc.Modules.Eth;

public class SimulateTxExecutor : ExecutorBase<IReadOnlyList<SimulateBlockResult>, SimulatePayload<TransactionForRpc>, SimulatePayload<TransactionWithSourceDetails>>
{
    private long gasCapBudget;
    private long blocksLimit;
    public SimulateTxExecutor(IBlockchainBridge blockchainBridge, IBlockFinder blockFinder, IJsonRpcConfig rpcConfig) :
        base(blockchainBridge, blockFinder, rpcConfig)
    {
        gasCapBudget = rpcConfig.GasCap ?? long.MaxValue;
        blocksLimit = rpcConfig.MaxSimulateBlocksCap ?? 256;
    }

    protected override SimulatePayload<TransactionWithSourceDetails> Prepare(SimulatePayload<TransactionForRpc> call)
    {
        SimulatePayload<TransactionWithSourceDetails>? result = new()
        {
            TraceTransfers = call.TraceTransfers,
            Validation = call.Validation,
            BlockStateCalls = call.BlockStateCalls?.Select(blockStateCall =>
            {
                if (blockStateCall.BlockOverrides?.GasLimit != null)
                {
                    blockStateCall.BlockOverrides.GasLimit = (ulong)Math.Min((long)blockStateCall.BlockOverrides.GasLimit!.Value, gasCapBudget);
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
                        callTransactionModel.EnsureDefaults(gasCapBudget);
                        gasCapBudget -= callTransactionModel.Gas!.Value;

                        var tx = callTransactionModel.ToTransaction(_blockchainBridge.GetChainId());

                        TransactionWithSourceDetails? result = new()
                        {
                            HadGasLimitInRequest = hadGasLimitInRequest,
                            HadNonceInRequest = hadNonceInRequest,
                            Transaction = tx
                        };

                        return result;
                    }).ToArray()
                };
            }).ToArray()
        };

        return result;
    }
    public override ResultWrapper<IReadOnlyList<SimulateBlockResult>> Execute(
        SimulatePayload<TransactionForRpc> call,
        BlockParameter? blockParameter)
    {

        if (call.BlockStateCalls == null)
        {
            return ResultWrapper<IReadOnlyList<SimulateBlockResult>>.Fail($"Must contain BlockStateCalls", ErrorCodes.InvalidParams);
        }

        if (call.BlockStateCalls!.Length > _rpcConfig.MaxSimulateBlocksCap)
        {
            return ResultWrapper<IReadOnlyList<SimulateBlockResult>>.Fail($"This node is configured to support only {_rpcConfig.MaxSimulateBlocksCap} blocks", ErrorCodes.InvalidInputTooManyBlocks);
        }
        SearchResult<Block> searchResult = _blockFinder.SearchForBlock(blockParameter);

        if (searchResult.IsError || searchResult.Object == null)
        {
            return ResultWrapper<IReadOnlyList<SimulateBlockResult>>.Fail(searchResult);
        }

        BlockHeader header = searchResult.Object.Header;

        if (blockParameter.Type == BlockParameterType.Latest)
        {
            header = _blockFinder.FindLatestBlock().Header;
        }

        if (!_blockchainBridge.HasStateForBlock(header!))
        {
            return ResultWrapper<IReadOnlyList<SimulateBlockResult>>.Fail($"No state available for block {header.Hash}", ErrorCodes.ResourceUnavailable);
        }

        if (call.BlockStateCalls?.Length > blocksLimit)
        {
            return ResultWrapper<IReadOnlyList<SimulateBlockResult>>.Fail($"Too many blocks provided, node is configured to simulate up to {blocksLimit} while {call.BlockStateCalls?.Length} were given", ErrorCodes.InvalidParams);
        }

        if (call.BlockStateCalls != null)
        {
            long lastBlockNumber = -1;
            ulong lastBlockTime = 0;

            foreach (BlockStateCall<TransactionForRpc>? blockToSimulate in call.BlockStateCalls!)
            {
                var givenNumber = blockToSimulate.BlockOverrides?.Number ?? (lastBlockNumber == -1 ? (ulong)header.Number + 1 : (ulong)lastBlockNumber + 1);

                if (givenNumber > long.MaxValue)
                {
                    return ResultWrapper<IReadOnlyList<SimulateBlockResult>>.Fail($"Block number too big {givenNumber}!", ErrorCodes.InvalidParams);
                }

                var given = (long)givenNumber;
                if (given > lastBlockNumber)
                {
                    lastBlockNumber = given;
                }
                else
                {
                    return ResultWrapper<IReadOnlyList<SimulateBlockResult>>.Fail($"Block number out of order {givenNumber}!", ErrorCodes.InvalidInputBlocksOutOfOrder);
                }

                if (blockToSimulate.BlockOverrides == null)
                {
                    blockToSimulate.BlockOverrides = new BlockOverride();
                }

                blockToSimulate.BlockOverrides!.Number = givenNumber;


                var givenTime = blockToSimulate.BlockOverrides?.Time ?? (lastBlockTime == 0 ? header.Timestamp + 1 : lastBlockTime + 1);

                if (givenTime > lastBlockTime)
                {
                    lastBlockTime = givenTime;
                }
                else
                {
                    return ResultWrapper<IReadOnlyList<SimulateBlockResult>>.Fail($"Block timestamp out of order {givenTime}!", ErrorCodes.InvalidInputBlocksOutOfOrder);
                }
                blockToSimulate.BlockOverrides!.Time = givenTime;

            }

            var minBlockNumber = Math.Min(
                    call.BlockStateCalls.Min(b =>
                        (long)(b.BlockOverrides?.Number ?? ulong.MaxValue)),
                    header.Number + 1);

            long maxBlockNumber = Math.Max(
                call.BlockStateCalls.Max(b =>
                    (long)(b.BlockOverrides?.Number ?? ulong.MinValue)),
                minBlockNumber);

            HashSet<long> existingBlockNumbers = new(call.BlockStateCalls.Select(b =>
                (long)(b.BlockOverrides?.Number ?? ulong.MinValue)));

            var completeBlockStateCalls = call.BlockStateCalls!.ToList();

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

            call.BlockStateCalls = completeBlockStateCalls.OrderBy(b => b.BlockOverrides!.Number!).ToArray();
        }

        using CancellationTokenSource cancellationTokenSource = new(_rpcConfig.Timeout); //TODO remove!
        SimulatePayload<TransactionWithSourceDetails>? toProcess = Prepare(call);
        return Execute(header.Clone(), toProcess, cancellationTokenSource.Token);
    }

    protected override ResultWrapper<IReadOnlyList<SimulateBlockResult>> Execute(BlockHeader header, SimulatePayload<TransactionWithSourceDetails> tx, CancellationToken token)
    {
        SimulateOutput results = _blockchainBridge.Simulate(header, tx, token);

        if (results.Error != null && results.Error.Contains("invalid transaction"))
        {
            results.ErrorCode = ErrorCodes.InvalidTransaction;
        }

        return results.Error is null
            ? ResultWrapper<IReadOnlyList<SimulateBlockResult>>.Success(results.Items)
            : results.ErrorCode != null
                ? ResultWrapper<IReadOnlyList<SimulateBlockResult>>.Fail(results.Error!, results.ErrorCode!.Value,
                    results.Items)
                : ResultWrapper<IReadOnlyList<SimulateBlockResult>>.Fail(results.Error, results.Items);
    }
}


