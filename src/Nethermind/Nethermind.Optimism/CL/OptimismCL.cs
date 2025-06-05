// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Optimism.CL.Decoding;
using Nethermind.Optimism.CL.L1Bridge;
using Nethermind.Optimism.CL.P2P;

namespace Nethermind.Optimism.CL;

public sealed class OptimismCL : IDisposable
{
    private readonly IDecodingPipeline _decodingPipeline;
    private readonly IL1Bridge _l1Bridge;
    private readonly IL1ConfigValidator _l1ConfigValidator;
    private readonly IL2Api _l2Api;
    private readonly IExecutionEngineManager _executionEngineManager;
    private readonly ITimestamper _timestamper;

    private readonly CLChainSpecEngineParameters _engineParameters;
    private readonly ulong _l2BlockTime;
    private readonly ulong _l2GenesisTimestamp;

    private readonly Driver _driver;
    private readonly OptimismCLP2P _p2p;

    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cancellationTokenSource = new();

    public OptimismCL(
        IDecodingPipeline decodingPipeline,
        IL1Bridge l1Bridge,
        IL1ConfigValidator l1ConfigValidator,
        IL2Api l2Api,
        IExecutionEngineManager executionEngineManager,
        ITimestamper timestamper,
        // Configs
        IOptimismConfig config,
        CLChainSpecEngineParameters engineParameters,
        IPAddress externalIp,
        ulong chainId,
        ulong l2GenesisTimestamp,
        ILogManager logManager
    )
    {
        ArgumentNullException.ThrowIfNull(config.L1BeaconApiEndpoint);
        ArgumentNullException.ThrowIfNull(config.L1EthApiEndpoint);
        ArgumentNullException.ThrowIfNull(engineParameters.UnsafeBlockSigner);
        ArgumentNullException.ThrowIfNull(engineParameters.Nodes);
        ArgumentNullException.ThrowIfNull(engineParameters.SystemConfigProxy);
        ArgumentNullException.ThrowIfNull(engineParameters.L2BlockTime);

        _logger = logManager.GetClassLogger();
        _engineParameters = engineParameters;
        _l2BlockTime = engineParameters.L2BlockTime.Value;
        _l2GenesisTimestamp = l2GenesisTimestamp;
        _timestamper = timestamper;

        _decodingPipeline = decodingPipeline;
        _l1Bridge = l1Bridge;
        _l1ConfigValidator = l1ConfigValidator;
        _l2Api = l2Api;
        _executionEngineManager = executionEngineManager;

        _driver = new Driver(
            _l1Bridge,
            _decodingPipeline,
            engineParameters,
            _executionEngineManager,
            _l2Api,
            chainId,
            l2GenesisTimestamp,
            logManager);
        _p2p = new OptimismCLP2P(
            _executionEngineManager,
            chainId,
            engineParameters.Nodes,
            config,
            engineParameters.UnsafeBlockSigner,
            timestamper,
            externalIp,
            logManager);
    }

    public async Task Start()
    {
        try
        {
            ArgumentNullException.ThrowIfNull(_engineParameters.L1ChainId);
            ArgumentNullException.ThrowIfNull(_engineParameters.L1GenesisNumber);
            ArgumentNullException.ThrowIfNull(_engineParameters.L1GenesisHash);

            bool isL1ConfigValid = await _l1ConfigValidator.Validate(
                _engineParameters.L1ChainId.Value,
                _engineParameters.L1GenesisNumber.Value,
                _engineParameters.L1GenesisHash);
            if (!isL1ConfigValid)
            {
                throw new InvalidOperationException("Invalid L1 config");
            }

            await _executionEngineManager.Initialize();

            L2Block? finalized = await _l2Api.GetFinalizedBlock();
            if (finalized is null || IsFinalizedHeadTooOld(finalized.Number))
            {
                Task p2pTask = _p2p.Run(_cancellationTokenSource.Token);
                await _executionEngineManager.OnELSynced;
                if (_logger.IsInfo) _logger.Info("EL sync completed. Starting Derivation Process");
                _driver.Reset((await _l2Api.GetHeadBlock()).Number);
                await _l1Bridge.Initialize(_cancellationTokenSource.Token);
                await Task.WhenAll(
                    p2pTask,
                    _decodingPipeline.Run(_cancellationTokenSource.Token),
                    _driver.Run(_cancellationTokenSource.Token)
                );
            }
            else
            {
                _l1Bridge.Reset(BlockId.FromL1BlockInfo(finalized.L1BlockInfo));
                _driver.Reset(finalized.Number);
                _p2p.Reset((await _l2Api.GetHeadBlock()).Number);
                await Task.WhenAll(
                    _p2p.Run(_cancellationTokenSource.Token),
                    _decodingPipeline.Run(_cancellationTokenSource.Token),
                    _driver.Run(_cancellationTokenSource.Token)
                );
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            if (_logger.IsWarn) _logger.Warn($"Exception in Optimism CL: {e}");
            throw;
        }
    }

    /// <summary>
    /// Checks if current head is too old to derive due to blob expiry
    /// </summary>
    private bool IsFinalizedHeadTooOld(ulong finalizedHeadNumber)
    {
        const ulong blobExpiryThresholdSeconds = 1124000; // around 13 days
        ulong currentTimeSeconds = _timestamper.UnixTime.Seconds;
        ulong finalizedBlockTimestamp = finalizedHeadNumber * _l2BlockTime + _l2GenesisTimestamp;
        return finalizedBlockTimestamp + blobExpiryThresholdSeconds < currentTimeSeconds;
    }

    public void Dispose()
    {
        _cancellationTokenSource.Cancel();
        _p2p.Dispose();
        _driver.Dispose();
    }
}
