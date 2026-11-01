// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.State.Flat.History.Prestate;

/// <summary>What a record reader needs to know about the fail-closed rules of the state it reads.</summary>
internal interface IPoisonedClearSource
{
    /// <summary>Moves when a capture publishes; a "not poisoned" answer sampled before it may no longer hold.</summary>
    long ClearsGeneration { get; }

    bool HasCapturedSince(long generation);

    /// <summary>Whether a slot read of <paramref name="address"/> at this state may fail closed because of a poisoned
    /// self-destruct above it.</summary>
    bool HasPoisonedClearAbove(Address address);
}

/// <summary>Remembers, per record account, whether its slot reads must go to history because of a poisoned clear.</summary>
/// <remarks>The memo belongs to the record, so it is shared by every reader of that state: a warm trace opens a new
/// bundle per transaction, and a per-reader memo would probe the clears column (an iterator seek) again in each one,
/// where a warm history read is served from the history read cache and probes nothing.</remarks>
/// <remarks>
/// The same rule as the history reader's own memo: "poisoned" is kept for good; "not poisoned" is kept with the capture
/// generation sampled before its probe, and trusted only while no capture has published since. Each entry carries its
/// own generation, so a probe that raced a publish can never be trusted after it, whatever order the threads that
/// share one reader write in. An entry is <c>0</c> when unknown, <see cref="Poisoned"/> when poisoned, and
/// <c>generation + 1</c> when not poisoned.
/// </remarks>
internal static class PoisonedClearMemo
{
    internal const long Poisoned = -1;

    public static bool HasPoisonedClearAbove<TSource>(long[] memo, int account, Address address, TSource source) where TSource : IPoisonedClearSource
    {
        long known = Volatile.Read(ref memo[account]);
        if (known == Poisoned) return true;
        if (known > 0 && !source.HasCapturedSince(known - 1)) return false;

        long sampled = source.ClearsGeneration;
        bool requires = source.HasPoisonedClearAbove(address);
        Volatile.Write(ref memo[account], requires ? Poisoned : sampled + 1);
        return requires;
    }
}
