// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Stateless;

/// <summary>
/// Lifetime helper coupling <see cref="IWitnessCaptureRegistry"/> with
/// <see cref="WitnessCapturingWorldStateProxy"/> for the duration of a single block.
/// Use with <c>using</c> so an unconsumed session (e.g. when <c>ProcessOne</c> throws)
/// disarms the proxy and cancels the pending capture automatically.
/// </summary>
/// <remarks>
/// Declared as a <c>ref struct</c>: the session holds mutable state (<c>_consumed</c>)
/// and an Armed-side-effect in its constructor, so a copy would silently break the
/// Drain/Dispose state machine. <c>ref struct</c> prevents the type from being boxed,
/// stored in heap fields, or captured by lambdas — making misuse a compile-time error.
/// </remarks>
public ref struct WitnessCaptureSession : IDisposable
{
    private readonly IWitnessCaptureRegistry? _registry;
    private readonly WitnessCapturingWorldStateProxy? _proxy;
    private readonly Hash256? _blockHash;
    private bool _consumed;

    private WitnessCaptureSession(IWitnessCaptureRegistry registry, WitnessCapturingWorldStateProxy proxy, Hash256 blockHash)
    {
        _registry = registry;
        _proxy = proxy;
        _blockHash = blockHash;
        proxy.Arm();
    }

    /// <summary>
    /// Arms the proxy if a capture is pending for <paramref name="blockHash"/> and processing is
    /// not read-only; otherwise returns a no-op session.
    /// </summary>
    public static WitnessCaptureSession TryArm(
        IWitnessCaptureRegistry? registry,
        WitnessCapturingWorldStateProxy? proxy,
        Hash256? blockHash,
        ProcessingOptions options)
    {
        if (registry is null || proxy is null || blockHash is null
            || options.ContainsFlag(ProcessingOptions.ReadOnlyChain)
            || !registry.HasPendingCapture(blockHash))
        {
            return default;
        }

        return new WitnessCaptureSession(registry, proxy, blockHash);
    }

    /// <summary>True when this session armed a capture and has not yet been drained or disposed.</summary>
    public readonly bool IsArmed => _proxy is not null && !_consumed;

    /// <summary>
    /// Builds the witness from the recorded state and completes the pending capture.
    /// If <paramref name="parentHeader"/> is null the capture is cancelled (no parent state
    /// root means no proof can be built). Safe to call on a no-op or already-drained session.
    /// </summary>
    public void Drain(BlockHeader? parentHeader)
    {
        if (_consumed || _proxy is null) return;
        _consumed = true;
        try
        {
            if (parentHeader is not null)
                _registry!.TryDrainCapture(_blockHash!, parentHeader, _proxy);
            else
                _registry!.DisarmCapture(_blockHash!);
        }
        finally
        {
            _proxy.Disarm();
        }
    }

    /// <summary>If not already drained, cancels the pending capture and disarms the proxy.</summary>
    public void Dispose()
    {
        if (_consumed || _proxy is null) return;
        _consumed = true;
        _registry!.DisarmCapture(_blockHash!);
        _proxy.Disarm();
    }
}
