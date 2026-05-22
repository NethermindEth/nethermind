// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus.Processing;
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
    private readonly WitnessCapturingWorldStateProxy? _proxy;
    private readonly Hash256? _blockHash;
    private readonly Hash256? _parentHash;
    private readonly Hash256? _parentStateRoot;
    private readonly long _parentBlockNumber;
    private bool _consumed;

    private WitnessCaptureSession(
        WitnessCapturingWorldStateProxy proxy,
        Hash256 blockHash,
        Hash256 parentHash,
        long parentBlockNumber)
    {
        _proxy = proxy;
        _blockHash = blockHash;
        _parentHash = parentHash;
        _parentBlockNumber = parentBlockNumber;
        _parentStateRoot = proxy.InnerStateRoot;
        proxy.Arm();
    }

    /// <summary>
    /// Arms the proxy if a capture is pending and not read-only; otherwise returns a no-op session.
    /// Also returns no-op when the proxy is already armed (nested decoration), so the outer
    /// session owns the lifecycle.
    /// </summary>
    public static WitnessCaptureSession TryArm(
        WitnessCapturingWorldStateProxy proxy,
        Hash256? blockHash,
        Hash256? parentHash,
        long blockNumber,
        ProcessingOptions options) =>
        blockHash is null || parentHash is null
            || options.ContainsFlag(ProcessingOptions.ReadOnlyChain)
            || !proxy.HasPendingRequest(blockHash)
            || proxy.IsArmed
            ? default
            : new WitnessCaptureSession(proxy, blockHash, parentHash, blockNumber - 1);

    /// <summary>True when this session armed a capture and has not yet been drained or disposed.</summary>
    public readonly bool IsArmed => _proxy is not null && !_consumed;

    /// <summary>
    /// Builds the witness from the recorded state and completes the pending capture.
    /// Safe to call repeatedly.
    /// </summary>
    public void Drain()
    {
        if (_consumed || _proxy is null) return;
        _consumed = true;
        try
        {
            _proxy.DrainTo(_blockHash!, _parentStateRoot!, _parentHash!, _parentBlockNumber);
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
        _proxy.CancelWitnessRequest(_blockHash!);
        _proxy.Disarm();
    }
}
