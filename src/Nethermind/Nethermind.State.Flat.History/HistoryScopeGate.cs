// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;

namespace Nethermind.State.Flat.History;

/// <summary>
/// Lets the window pruner wait for read scopes admitted under an old floor before deleting the rows they may still
/// be reading. Each scope is counted on its floor generation; a drain waits only for generations below the current one.
/// </summary>
/// <remarks>
/// <see cref="TryDrainForFloorAdvance"/> must be called AFTER publishing the new floor. Opening and closing a scope
/// only changes the count of its generation, so the parallel readers of one node never take a lock here.
/// </remarks>
public sealed class HistoryScopeGate
{
    private readonly Lock _advanceLock = new();
    // Newest first. A generation stays linked while it is the newest or has open scopes.
    private Generation _newest = new(0);

    /// <summary>Returns the token for the matching <see cref="ExitScope"/>. A registration that races a drain may
    /// be missed by its sweep, which is safe: the caller validates the floor after this returns.</summary>
    public long EnterScope()
    {
        while (true)
        {
            Generation generation = Volatile.Read(ref _newest);
            Interlocked.Increment(ref generation.OpenScopes);

            // Counted on a generation that an advance has since replaced: the advance may already have found it
            // empty and unlinked it, so the scope is counted on the newest one instead, whose floor it then sees.
            if (ReferenceEquals(generation, Volatile.Read(ref _newest))) return generation.Number;

            Interlocked.Decrement(ref generation.OpenScopes);
        }
    }

    public void ExitScope(long scopeId)
    {
        for (Generation? generation = Volatile.Read(ref _newest); generation is not null; generation = Volatile.Read(ref generation.Older))
        {
            if (generation.Number == scopeId)
            {
                Interlocked.Decrement(ref generation.OpenScopes);
                return;
            }
        }
    }

    /// <summary>Bumps the floor generation. A retried drain must re-wait on the generation it got here rather than
    /// bump again, which would also demote scopes already safe by their own admission check.</summary>
    internal long BeginFloorAdvance()
    {
        lock (_advanceLock)
        {
            Generation previous = _newest;
            Generation next = new(previous.Number + 1) { Older = previous };
            // A full fence before the counts are read below, so a scope that saw the previous generation as the
            // newest after counting itself on it is seen here and keeps it linked.
            Interlocked.Exchange(ref _newest, next);
            UnlinkClosedGenerations(next);
            return next.Number;
        }
    }

    // A scope whose count lands on an unlinked generation finds it replaced and counts itself again on the newest,
    // and a walk standing on an unlinked generation still reaches every older one through it.
    private static void UnlinkClosedGenerations(Generation newest)
    {
        Generation linked = newest;
        for (Generation? older = linked.Older; older is not null; older = older.Older)
        {
            if (Volatile.Read(ref older.OpenScopes) == 0) continue;

            Volatile.Write(ref linked.Older, older);
            linked = older;
        }

        Volatile.Write(ref linked.Older, null);
    }

    /// <summary>Bumps and drains in one step - the shape every first attempt wants.</summary>
    internal bool TryDrainForFloorAdvance(TimeSpan timeout, CancellationToken token) =>
        TryDrain(BeginFloorAdvance(), timeout, token);

    /// <summary>Waits, bounded, for every scope older than <paramref name="generation"/> to close.</summary>
    internal bool TryDrain(long generation, TimeSpan timeout, CancellationToken token)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (AnyScopeBelow(generation))
        {
            if (token.IsCancellationRequested || stopwatch.Elapsed >= timeout) return false;

            Thread.Sleep(10);
        }

        return true;
    }

    private bool AnyScopeBelow(long generation)
    {
        for (Generation? linked = Volatile.Read(ref _newest); linked is not null; linked = Volatile.Read(ref linked.Older))
        {
            if (linked.Number < generation && Volatile.Read(ref linked.OpenScopes) > 0) return true;
        }

        return false;
    }

    private sealed class Generation(long number)
    {
        public readonly long Number = number;
        public int OpenScopes;
        public Generation? Older;
    }
}
