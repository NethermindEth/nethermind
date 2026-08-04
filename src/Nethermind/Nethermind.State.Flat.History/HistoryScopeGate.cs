// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;

namespace Nethermind.State.Flat.History;

/// <summary>
/// Lock-free epoch counter that lets the window pruner wait for historical read scopes admitted under an old
/// floor to finish before it deletes the rows they may still be reading. A scope opened when the floor was F_old
/// can still be mid-read when the pruner publishes F_new and starts deleting toward it; without this drain, that
/// read could see a row disappear mid-flight and (for a fall-through-style read) resolve to the wrong answer
/// instead of failing closed. Scope entry/exit is Interlocked-only — no lock on the read path — so opening a new
/// scope is never blocked by a draining pruner.
/// </summary>
/// <remarks>
/// The pruner must call <see cref="TryDrainForFloorAdvance"/> AFTER publishing the new floor, not before: any
/// scope opened after publish already sees the new floor at its own admission check (<c>HistoricalFlatDbManager</c>
/// re-reads the floor on every scope open, never a cached value), so it is safe by construction regardless of
/// which epoch it lands in. The only scopes that need draining are ones admitted before publish, under the old,
/// lower floor. Calling this before publish would let a scope open in the gap between the flip and the publish
/// join the "new" epoch and never be waited on, even though it read under the old floor.
/// </remarks>
public sealed class HistoryScopeGate
{
    private long _epoch0Active;
    private long _epoch1Active;
    private int _currentEpoch;

    /// <summary>
    /// Marks a historical read scope as open, returning the epoch it joined for the matching
    /// <see cref="ExitScope"/> call. Increment-then-validate against a concurrent <see cref="FlipEpoch"/>: a scope
    /// that raced a flip mid-registration retries into whichever epoch is current once its own increment is
    /// visible, so the drain can never sample a count of zero while a scope is still completing its entry.
    /// </summary>
    public int EnterScope()
    {
        while (true)
        {
            int epoch = Volatile.Read(ref _currentEpoch);
            Increment(epoch);
            if (Volatile.Read(ref _currentEpoch) == epoch) return epoch;

            // A flip landed between the read and the increment: this registration may already be invisible to
            // whichever epoch the flip moved to. Undo and retry against the now-current epoch.
            Decrement(epoch);
        }
    }

    /// <summary>Marks the scope opened under <paramref name="epoch"/> (the value <see cref="EnterScope"/>
    /// returned) as closed.</summary>
    public void ExitScope(int epoch) => Decrement(epoch);

    /// <summary>
    /// Flips new scope registrations onto the other epoch slot, waits (bounded) for every scope counted in the
    /// epoch active at the moment of the flip to close, and reports whether the drain completed. On timeout or
    /// cancellation, restores the pre-flip epoch so a retried call keeps targeting the same census — without this,
    /// a second call would flip past an already-stuck scope and wait on an unrelated (and already-empty) slot
    /// while the stuck scope's epoch is never observed again.
    /// </summary>
    internal bool TryDrainForFloorAdvance(TimeSpan timeout, CancellationToken token)
    {
        int drainEpoch = FlipEpoch();
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (ActiveInEpoch(drainEpoch) > 0)
        {
            if (token.IsCancellationRequested || stopwatch.Elapsed >= timeout)
            {
                Volatile.Write(ref _currentEpoch, drainEpoch);
                return false;
            }

            Thread.Sleep(10);
        }

        return true;
    }

    private int FlipEpoch()
    {
        int previous = Volatile.Read(ref _currentEpoch);
        Volatile.Write(ref _currentEpoch, 1 - previous);
        return previous;
    }

    private long ActiveInEpoch(int epoch) => epoch == 0 ? Volatile.Read(ref _epoch0Active) : Volatile.Read(ref _epoch1Active);

    private void Increment(int epoch)
    {
        if (epoch == 0) Interlocked.Increment(ref _epoch0Active);
        else Interlocked.Increment(ref _epoch1Active);
    }

    private void Decrement(int epoch)
    {
        if (epoch == 0) Interlocked.Decrement(ref _epoch0Active);
        else Interlocked.Decrement(ref _epoch1Active);
    }
}
