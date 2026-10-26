// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
    private int _externalCallsInFlight;
    private readonly Lock _engineCallLock = new();
    private int _embeddedCallsInFlight;
    private TaskCompletionSource? _embeddedCallsDrained;

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
            if (Volatile.Read(ref _inner) is null)
            {
                _ = engineRpcModule.Value;
            }

            return Volatile.Read(ref _inner) ?? throw new InvalidOperationException($"{nameof(ExternalClInterceptingEngineRpcModule)} is not decorating {nameof(IEngineRpcModule)}");
        }
    }

    /// <summary>Whether an external consensus client took over and the embedded driver must send no more engine calls.</summary>
    internal bool HasStoodDown => config.DisableOnExternalCl && IsExternalClDetected;

    /// <exception cref="OperationCanceledException">An external consensus client took over the engine API.</exception>
    /// <exception cref="EngineUnavailableException">An external call that may take over is still in flight, so <paramref name="method"/> is retried later.</exception>
    internal void ThrowIfStoodDown(string method)
    {
        if (HasStoodDown)
            throw new OperationCanceledException("External consensus client took over the engine API");
        // Forwarded meanwhile, the driver's call would race the external client's for the execution head.
        if (config.DisableOnExternalCl && Volatile.Read(ref _externalCallsInFlight) != 0)
            throw new EngineUnavailableException(method, "an external consensus client call is in flight");
    }

    public void SetInner(IEngineRpcModule inner) => Volatile.Write(ref _inner, inner);

    internal IDisposable EnterEmbeddedCall(string method)
    {
        lock (_engineCallLock)
        {
            ThrowIfStoodDown(method);
            _embeddedCallsInFlight++;
            return new EmbeddedCall(this);
        }
    }

    private sealed class EmbeddedCall(ExternalClDetector detector) : IDisposable
    {
        public void Dispose()
        {
            lock (detector._engineCallLock)
            {
                if (--detector._embeddedCallsInFlight == 0)
                {
                    detector._embeddedCallsDrained?.TrySetResult();
                    detector._embeddedCallsDrained = null;
                }
            }
        }
    }

    /// <summary>When <see cref="IBeaconChainConfig.DisableOnExternalCl"/> is enabled, prevents new embedded calls and waits for existing ones before forwarding an external call.</summary>
    /// <remarks>
    /// The wait has no independent timeout: an embedded caller's timeout does not cancel its underlying
    /// execution work. Forwarding before that work finishes could let it overwrite the external client's head.
    /// </remarks>
    internal Task OnExternalEngineCallStarted()
    {
        lock (_engineCallLock)
        {
            Interlocked.Increment(ref _externalCallsInFlight);
            return !config.DisableOnExternalCl ? Task.CompletedTask : WaitForEmbeddedCallsAsync();
        }
    }

    internal Task WaitForEmbeddedCallsAsync()
    {
        lock (_engineCallLock)
        {
            return _embeddedCallsInFlight == 0
                ? Task.CompletedTask
                : (_embeddedCallsDrained ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
    }

    /// <summary>Called by the decorator when a call reported by <see cref="OnExternalEngineCallStarted"/> ends.</summary>
    /// <remarks>Only an accepted call detects the external client, so a rejected one leaves the driver running.</remarks>
    internal void OnExternalEngineCallEnded(bool accepted)
    {
        // Detection is visible before the count drops, so no driver check falls between the two.
        if (accepted) OnExternalEngineCall();
        Interlocked.Decrement(ref _externalCallsInFlight);
    }

    /// <summary>Records an accepted external <c>newPayload</c>/<c>forkchoiceUpdated</c>.</summary>
    public void OnExternalEngineCall()
    {
        if (Interlocked.Exchange(ref _detected, 1) != 0)
        {
            return;
        }

        if (_logger.IsWarn) _logger.Warn(config.DisableOnExternalCl
            ? "External consensus client detected on the engine API - disabling the embedded beacon chain driver until restart"
            : "External consensus client detected while the embedded beacon chain driver remains enabled; both clients may update the execution head");
        if (config.DisableOnExternalCl) ExternalClDetected?.Invoke();
    }
}
