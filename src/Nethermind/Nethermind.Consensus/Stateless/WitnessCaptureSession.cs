// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;

namespace Nethermind.Consensus.Stateless;

/// <summary>
/// Per-main-processing-scope arming point for witness capture. Holds nullable pointers to the
/// recorders that are active during a single <c>ProcessOne</c> call; the decorators
/// (<see cref="WitnessCapturingWorldStateProxy"/>, <see cref="WitnessCapturingHeaderFinder"/>)
/// consult these pointers on every call and forward straight through to the inner component when null.
/// </summary>
/// <remarks>
/// <para>
/// Single arm/disarm point in <see cref="WitnessCapturingBlockProcessor"/> replaces the previous
/// per-decorator <c>TryActivate</c>/<c>Deactivate</c> ceremony: all three decorators become dumb
/// passthroughs that share one source of truth here.
/// </para>
/// <para>
/// Thread-safety: <see cref="TryArm"/> uses CAS on the world-state pointer so concurrent armers
/// see at most one winner; <see cref="Disarm"/> clears in reverse so any consumer that still sees
/// <see cref="WorldStateRecorder"/> set also sees the other recorders set. The main processing
/// pipeline drives blocks serially so contention is theoretical, but the volatile reads remain
/// safe under any caller.
/// </para>
/// </remarks>
public sealed class WitnessCaptureSession
{
    private WitnessGeneratingWorldState? _worldStateRecorder;
    private WitnessHeaderRecorder? _headerRecorder;

    public WitnessGeneratingWorldState? WorldStateRecorder => Volatile.Read(ref _worldStateRecorder);
    public WitnessHeaderRecorder? HeaderRecorder => Volatile.Read(ref _headerRecorder);

    public bool IsActive => WorldStateRecorder is not null;

    /// <summary>
    /// Atomically installs the two recorders for a single capture pass. Returns <c>false</c>
    /// when a capture is already in progress on this session.
    /// </summary>
    /// <remarks>
    /// The world-state recorder is the primary slot — the CAS on it gates the operation; the other
    /// is written under the post-CAS happens-before, so any reader that observes the
    /// world-state recorder also observes the header recorder.
    /// </remarks>
    public bool TryArm(
        WitnessGeneratingWorldState worldStateRecorder,
        WitnessHeaderRecorder headerRecorder)
    {
        Volatile.Write(ref _headerRecorder, headerRecorder);
        return Interlocked.CompareExchange(ref _worldStateRecorder, worldStateRecorder, null) is null;
    }

    public void Disarm()
    {
        Volatile.Write(ref _worldStateRecorder, null);
        Volatile.Write(ref _headerRecorder, null);
    }
}
