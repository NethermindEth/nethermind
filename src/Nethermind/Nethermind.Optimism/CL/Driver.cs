// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Logging;
using Nethermind.Optimism.CL.Decoding;
using Nethermind.Optimism.CL.Derivation;
using Nethermind.Optimism.CL.L1Bridge;

namespace Nethermind.Optimism.CL;

public class Driver : IDisposable
{
    private readonly ILogger _logger;
    private readonly IDerivationPipeline _derivationPipeline;
    private readonly IL2Api _l2Api;
    private readonly IExecutionEngineManager _executionEngineManager;
    private readonly IDecodingPipeline _decodingPipeline;
    private readonly ISystemConfigDeriver _systemConfigDeriver;
    private readonly IL1Bridge _l1Bridge;
    private readonly ulong _l2BlockTime;

    public Driver(
        IL1Bridge l1Bridge,
        IDecodingPipeline decodingPipeline,
        CLChainSpecEngineParameters engineParameters,
        IExecutionEngineManager executionEngineManager,
        IL2Api l2Api,
        ulong chainId,
        ulong l2GenesisTimestamp,
        ILogManager logManager)
    {
        ArgumentNullException.ThrowIfNull(engineParameters.L2BlockTime);
        ArgumentNullException.ThrowIfNull(engineParameters.SystemConfigProxy);
        _l1Bridge = l1Bridge;
        _l2BlockTime = engineParameters.L2BlockTime.Value;
        _logger = logManager.GetClassLogger();
        _l2Api = l2Api;
        _decodingPipeline = decodingPipeline;
        _systemConfigDeriver = new SystemConfigDeriver(engineParameters.SystemConfigProxy);
        _executionEngineManager = executionEngineManager;

        _derivationPipeline = new DerivationPipeline(
            new PayloadAttributesDeriver(
                _systemConfigDeriver,
                new DepositTransactionBuilder(chainId, engineParameters)),
            l1Bridge,
            l2GenesisTimestamp,
            _l2BlockTime,
            chainId,
            logManager);
    }

    private ulong _currentDerivedBlock;
    private ulong _currentFinalizedL1Block;

    public async Task Run(CancellationToken token)
    {
        try
        {
            Task<L1BridgeStepResult> l1BridgeStep = _l1Bridge.Step(token);
            while (!token.IsCancellationRequested)
            {
                Task nextBatchReady = _decodingPipeline.DecodedBatchesReader.WaitToReadAsync(token).AsTask();

                await Task.WhenAny(l1BridgeStep, nextBatchReady);

                if (nextBatchReady.IsCompleted)
                {
                    (BatchV1 decodedBatch, ulong batchOrigin) = await _decodingPipeline.DecodedBatchesReader.ReadAsync(token);
                    await ProcessDecodedBatch(decodedBatch, batchOrigin, token);
                    continue;
                }

                if (l1BridgeStep.IsCompleted)
                {
                    L1BridgeStepResult result = l1BridgeStep.Result;
                    if (_logger.IsInfo) _logger.Info(result.ToString());
                    switch (result.Type)
                    {
                        case L1BridgeStepResultType.Block:
                            {
                                foreach (DaDataSource daDataSource in result.NewData!)
                                {
                                    await _decodingPipeline.DaDataWriter.WriteAsync(daDataSource, token);
                                }
                                l1BridgeStep = _l1Bridge.Step(token);
                                break;
                            }
                        case L1BridgeStepResultType.Finalization:
                            {
                                await ProcessNewFinalized(result.NewFinalized!.Value, token);
                                l1BridgeStep = _l1Bridge.Step(token);
                                break;
                            }
                        case L1BridgeStepResultType.Reorg:
                            {
                                await ProcessReorg(token);
                                l1BridgeStep = _l1Bridge.Step(token);
                                break;
                            }
                        case L1BridgeStepResultType.Skip:
                            {
                                l1BridgeStep = Task.Run(async () =>
                                {
                                    await Task.Delay(12000, token);
                                    return await _l1Bridge.Step(token);
                                }, token);
                                break;
                            }
                    }

                    continue;
                }
            }
        }
        catch (Exception e)
        {
            if (_logger.IsWarn && e is not OperationCanceledException)
                _logger.Warn($"Unhandled exception in Driver: {e}");
        }
        finally
        {
            if (_logger.IsInfo) _logger.Info("Driver is shutting down.");
        }
    }

    private async Task ProcessReorg(CancellationToken token)
    {
        L2Block? block = await _l2Api.GetFinalizedBlock();
        ArgumentNullException.ThrowIfNull(block);
        Reset(block.Number);
        _l1Bridge.Reset(BlockId.FromL1BlockInfo(block.L1BlockInfo));
        await _decodingPipeline.Reset(token);
    }

    private readonly Queue<(ulong L1BatchOrigin, BlockId LastL2Block)> _safeBlocksQueue = new();

    private async Task ProcessNewFinalized(ulong newFinalized, CancellationToken token)
    {
        if (_logger.IsInfo) _logger.Info($"Processing new finalized L1 block. {newFinalized}");
        _currentFinalizedL1Block = newFinalized;
        BlockId? lastL2Block = null;
        while (_safeBlocksQueue.Any())
        {
            (ulong l1BatchOrigin, BlockId last) = _safeBlocksQueue.Peek();
            if (l1BatchOrigin > newFinalized)
            {
                break;
            }
            lastL2Block = last;
            _safeBlocksQueue.Dequeue();
        }

        if (lastL2Block is not null)
        {
            await _executionEngineManager.FinalizeBlock(lastL2Block.Value, token);
        }
    }

    private async Task ProcessDecodedBatch(BatchV1 decodedBatch, ulong batchOrigin, CancellationToken token)
    {
        ulong firstBlockNumber = decodedBatch.RelTimestamp / _l2BlockTime;
        ulong lastBlockNumber = firstBlockNumber + decodedBatch.BlockCount - 1;
        if (_logger.IsInfo)
            _logger.Info($"Got batch for processing. Blocks from {firstBlockNumber} to {lastBlockNumber}");
        if (lastBlockNumber <= _currentDerivedBlock)
        {
            if (_logger.IsInfo) _logger.Info("Old batch. Skipping");
            return;
        }

        if (_currentDerivedBlock + 1 < firstBlockNumber)
        {
            if (_logger.IsWarn)
                _logger.Warn(
                    $"Derived batch is out of order. Highest derived block: {_currentDerivedBlock}, Batch first block: {firstBlockNumber}");
            throw new ArgumentException("Batch is out of order");
        }

        L2Block l2Parent = await _l2Api.GetBlockByNumber(firstBlockNumber - 1);

        var derivedPayloadAttributes = _derivationPipeline
            .DerivePayloadAttributes(l2Parent, decodedBatch, token)
            .GetAsyncEnumerator(token);
        BlockId? lastDerivedBlock = null;
        while (await derivedPayloadAttributes.MoveNextAsync())
        {
            PayloadAttributesRef payloadAttributes = derivedPayloadAttributes.Current;
            BlockId? derivedBlock = await _executionEngineManager.ProcessNewDerivedPayloadAttributes(payloadAttributes, token);
            if (derivedBlock is null)
            {
                if (_logger.IsWarn) _logger.Warn($"Derived invalid Payload Attributes. {payloadAttributes}");
                break;
            }

            if (batchOrigin <= _currentFinalizedL1Block)
            {
                await _executionEngineManager.FinalizeBlock(derivedBlock.Value, token);
            }

            lastDerivedBlock = derivedBlock.Value;
            _currentDerivedBlock = payloadAttributes.Number;
        }

        if (lastDerivedBlock is not null) _safeBlocksQueue.Enqueue((batchOrigin, lastDerivedBlock.Value));
    }

    public void Reset(ulong finalizedBlockNumber)
    {
        if (_logger.IsInfo) _logger.Info($"Resetting Driver. New finalized block {finalizedBlockNumber}");
        _currentDerivedBlock = finalizedBlockNumber;
        _safeBlocksQueue.Clear();
    }

    public void Dispose()
    {
    }
}
