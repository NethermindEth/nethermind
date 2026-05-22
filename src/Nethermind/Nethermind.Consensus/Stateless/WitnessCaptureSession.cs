// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Stateless;

/// <summary>
/// Arm-on-construct, drain-or-disarm-on-dispose session for a single block's witness capture.
/// </summary>
/// <remarks>
/// <c>ref struct</c> so a copy can't silently split the Drain/Dispose state.
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
    /// Arms the proxy if a capture is pending and not read-only; otherwise returns a no-op session.
    /// </summary>
    public static WitnessCaptureSession TryArm(
        IWitnessCaptureRegistry? registry,
        WitnessCapturingWorldStateProxy? proxy,
        Hash256? blockHash,
        ProcessingOptions options) =>
        registry is null || proxy is null || blockHash is null
            || options.ContainsFlag(ProcessingOptions.ReadOnlyChain)
            || !registry.HasPendingCapture(blockHash)
            ? default
            : new WitnessCaptureSession(registry, proxy, blockHash);

    /// <summary>
    /// True when this session armed a capture and has not yet been drained or disposed.
    /// </summary>
    public readonly bool IsArmed => _proxy is not null && !_consumed;

    /// <summary>
    /// Builds the witness and completes the capture. With a null <paramref name="parentHeader"/>
    /// the capture is cancelled — no parent state root, no provable proof. Safe to call repeatedly.
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

    /// <summary>
    /// If not already drained, cancels the pending capture and disarms the proxy.
    /// </summary>
    public void Dispose()
    {
        if (_consumed || _proxy is null) return;
        _consumed = true;
        _registry!.DisarmCapture(_blockHash!);
        _proxy.Disarm();
    }
}
