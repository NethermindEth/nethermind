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
/// Thread-safety: both recorders are packed into one immutable slot that <see cref="TryArm"/>
/// installs with a single CAS, so a reader observes either the fully-armed pair or nothing — there
/// is no window where one recorder is visible without the other, and a losing armer cannot clobber
/// the winner's recorders. <see cref="Disarm"/> clears the slot with one atomic write. The main
/// processing pipeline drives blocks serially so contention is theoretical, but these guarantees
/// hold under any caller.
/// </para>
/// </remarks>
public sealed class WitnessCaptureSession
{
    private Recorders? _recorders;

    public WitnessGeneratingWorldState? WorldStateRecorder => Volatile.Read(ref _recorders)?.WorldState;
    public WitnessHeaderRecorder? HeaderRecorder => Volatile.Read(ref _recorders)?.Header;

    public bool IsActive => Volatile.Read(ref _recorders) is not null;

    /// <summary>
    /// Atomically installs the two recorders for a single capture pass. Returns <c>false</c>
    /// when a capture is already in progress on this session.
    /// </summary>
    /// <remarks>
    /// Both recorders are bundled into one immutable slot installed by a single
    /// <see cref="Interlocked.CompareExchange{T}(ref T, T, T)"/>, so the pair is published
    /// atomically: a concurrent reader sees either both recorders (via the load-acquire in the
    /// property getters) or neither, and a losing armer leaves the winner's slot untouched.
    /// </remarks>
    public bool TryArm(
        WitnessGeneratingWorldState worldStateRecorder,
        WitnessHeaderRecorder headerRecorder)
        => Interlocked.CompareExchange(ref _recorders, new Recorders(worldStateRecorder, headerRecorder), null) is null;

    public void Disarm() => Volatile.Write(ref _recorders, null);

    private sealed record Recorders(WitnessGeneratingWorldState WorldState, WitnessHeaderRecorder Header);
}
