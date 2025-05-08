// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
    private readonly ulong _l2BlockTime;

    public Driver(IL1Bridge l1Bridge,
        IDecodingPipeline decodingPipeline,
        CLChainSpecEngineParameters engineParameters,
        IExecutionEngineManager executionEngineManager,
        IL2Api l2Api,
        ulong chainId,
        ulong l2GenesisTimestamp,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(engineParameters.L2BlockTime);
        ArgumentNullException.ThrowIfNull(engineParameters.SystemConfigProxy);
        _l2BlockTime = engineParameters.L2BlockTime.Value;
        _logger = logger;
        _l2Api = l2Api;
        _decodingPipeline = decodingPipeline;
        _systemConfigDeriver = new SystemConfigDeriver(engineParameters.SystemConfigProxy);
        _executionEngineManager = executionEngineManager;
        var payloadAttributesDeriver = new PayloadAttributesDeriver(
            _systemConfigDeriver,
            new DepositTransactionBuilder(chainId, engineParameters),
            logger);
        _derivationPipeline = new DerivationPipeline(payloadAttributesDeriver, l1Bridge,
            l2GenesisTimestamp, _l2BlockTime, chainId, _logger);
    }

    private ulong _currentDerivedBlock;

    public async Task Run(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                BatchV1 decodedBatch = await _decodingPipeline.DecodedBatchesReader.ReadAsync(token);

                ulong firstBlockNumber = decodedBatch.RelTimestamp / _l2BlockTime;
                ulong lastBlockNumber = firstBlockNumber + decodedBatch.BlockCount - 1;
                if (_logger.IsInfo)
                    _logger.Info($"Got batch for processing. Blocks from {firstBlockNumber} to {lastBlockNumber}");
                if (lastBlockNumber <= _currentDerivedBlock)
                {
                    if (_logger.IsInfo) _logger.Info("Got old batch. Skipping");
                    continue;
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
                while (await derivedPayloadAttributes.MoveNextAsync())
                {
                    PayloadAttributesRef payloadAttributes = derivedPayloadAttributes.Current;
                    bool valid = await _executionEngineManager.ProcessNewDerivedPayloadAttributes(payloadAttributes);
                    if (!valid)
                    {
                        if (_logger.IsWarn) _logger.Warn($"Derived invalid Payload Attributes. {payloadAttributes}");
                        break;
                    }

                    _currentDerivedBlock = payloadAttributes.Number;
                }
            }
        }
        catch (Exception e)
        {
            if (_logger.IsWarn) _logger.Warn($"Unhandled exception in Driver: {e}");
        }
    }

    public void Reset(ulong finalizedBlockNumber)
    {
        if (_logger.IsInfo) _logger.Info($"Resetting Driver. New finalized block {finalizedBlockNumber}");
        _currentDerivedBlock = finalizedBlockNumber;
    }

    public void Dispose()
    {
    }
}
