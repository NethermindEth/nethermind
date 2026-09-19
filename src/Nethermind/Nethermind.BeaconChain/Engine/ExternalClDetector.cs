// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;

namespace Nethermind.BeaconChain.Engine;

/// <summary>
/// Detects an external consensus client driving the engine API and hands the embedded driver the
/// undecorated engine module so its own calls bypass detection.
/// </summary>
/// <remarks>
/// <see cref="ExternalClInterceptingEngineRpcModule"/> reports every externally issued
/// <c>newPayload</c>/<c>forkchoiceUpdated</c> here and populates <see cref="InnerEngine"/> with
/// the module it decorates.
/// </remarks>
public sealed class ExternalClDetector(
    IBeaconChainConfig config,
    Lazy<IEngineRpcModule> engineRpcModule,
    ILogManager logManager)
{
    private readonly ILogger _logger = logManager.GetClassLogger<ExternalClDetector>();
    private IEngineRpcModule? _inner;
    private int _detected;

    /// <summary>Raised on the first external engine call when <see cref="IBeaconChainConfig.DisableOnExternalCl"/> is set; the driver lifecycle stops itself in response.</summary>
    public event Action? ExternalClDetected;

    /// <summary>Whether any <c>newPayload</c>/<c>forkchoiceUpdated</c> call has arrived from outside the embedded driver.</summary>
    public bool IsExternalClDetected => Volatile.Read(ref _detected) != 0;

    /// <summary>The undecorated engine RPC module for the embedded driver's own calls, which must not trip detection.</summary>
    /// <remarks>
    /// Populated by the decorator's constructor. The getter forces the decorator chain to be
    /// constructed by touching the lazy container resolution, so the driver can call in before any
    /// external request resolved the module.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The decorator is not registered over the engine RPC module.</exception>
    public IEngineRpcModule InnerEngine
    {
        get
        {
            if (_inner is null)
            {
                _ = engineRpcModule.Value;
            }

            return _inner ?? throw new InvalidOperationException($"{nameof(ExternalClInterceptingEngineRpcModule)} is not decorating {nameof(IEngineRpcModule)}");
        }
    }

    public void SetInner(IEngineRpcModule inner) => _inner = inner;

    /// <summary>Called by the decorator on every externally visible <c>newPayload</c>/<c>forkchoiceUpdated</c>.</summary>
    public void OnExternalEngineCall()
    {
        if (Interlocked.Exchange(ref _detected, 1) != 0 || !config.DisableOnExternalCl)
        {
            return;
        }

        if (_logger.IsWarn) _logger.Warn("External consensus client detected on the engine API — disabling the embedded beacon chain driver");
        ExternalClDetected?.Invoke();
    }
}
