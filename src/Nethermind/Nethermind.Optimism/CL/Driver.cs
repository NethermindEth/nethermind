// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;
using Nethermind.Optimism.CL.Decoding;
using Nethermind.Optimism.CL.Derivation;
using Nethermind.Optimism.CL.L1Bridge;
using Nethermind.Optimism.Rpc;

namespace Nethermind.Optimism.CL;

public class Driver : IDisposable
{
    private readonly ILogger _logger;
    private readonly IDerivationPipeline _derivationPipeline;
    private readonly IL2Api _il2Api;
    private readonly IDecodingPipeline _decodingPipeline;
    private readonly ISystemConfigDeriver _systemConfigDeriver;

    public Driver(IL1Bridge l1Bridge,
        IDecodingPipeline decodingPipeline,
        CLChainSpecEngineParameters engineParameters,
        IExecutionEngineManager executionEngineManager,
        IL2Api il2Api,
        ulong chainId,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(engineParameters.L2BlockTime);
        _logger = logger;
        _il2Api = il2Api;
        _decodingPipeline = decodingPipeline;
        _systemConfigDeriver = new SystemConfigDeriver(engineParameters);
        var payloadAttributesDeriver = new PayloadAttributesDeriver(
            _systemConfigDeriver,
            new DepositTransactionBuilder(chainId, engineParameters),
            logger);
        _derivationPipeline = new DerivationPipeline(payloadAttributesDeriver, l1Bridge, executionEngineManager, _logger);
    }

    public async Task Run(CancellationToken token)
    {
        await Task.WhenAll(
            _derivationPipeline.Run(token),
            MainLoop(token)
        );
    }

    private async Task MainLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            BatchV1 decodedBatch = await _decodingPipeline.DecodedBatchesReader.ReadAsync(token);

            ulong parentNumber = decodedBatch.RelTimestamp / 2 - 1;
            _logger.Error($"Running derivation. Parent number: {parentNumber}");
            L2Block l2Parent = _il2Api.GetBlockByNumber(parentNumber);

            await _derivationPipeline.BatchesForProcessing.WriteAsync((l2Parent, decodedBatch), token);
        }
    }

    public void Dispose()
    {
    }
}
