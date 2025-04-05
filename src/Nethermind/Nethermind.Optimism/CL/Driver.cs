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
            l2GenesisTimestamp, engineParameters.L2BlockTime.Value, chainId, _logger);
    }

    public async Task Run(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            BatchV1 decodedBatch = await _decodingPipeline.DecodedBatchesReader.ReadAsync(token);

            ulong parentNumber = decodedBatch.RelTimestamp / 2 - 1;
            L2Block l2Parent = await _l2Api.GetBlockByNumber(parentNumber);

            PayloadAttributesRef[] derivedPayloadAttributes = await _derivationPipeline.DerivePayloadAttributes(l2Parent, decodedBatch, token);
            foreach (PayloadAttributesRef payloadAttributes in derivedPayloadAttributes)
            {
                bool valid = await _executionEngineManager.ProcessNewDerivedPayloadAttributes(payloadAttributes);
                if (!valid)
                {
                    if (_logger.IsWarn) _logger.Warn($"Derived invalid Payload Attributes. {payloadAttributes}");
                    break;
                }
            }

        }
    }

    public void Dispose()
    {
    }
}
