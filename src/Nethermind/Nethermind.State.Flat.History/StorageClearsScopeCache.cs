// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Core.Crypto;

namespace Nethermind.State.Flat.History;

/// <summary>
/// Per-scope memo of an account's self-destruct markers: whether any lies at or before the scope's block, and the
/// lowest poisoned clear above it.
/// </summary>
/// <remarks>The poisoned-clear range lies above the scope's block, so a capture can still add to it: a negative
/// is trusted only while no capture has published since it was probed. Each memo is created on its first use: a
/// history format uses only one of them, and a scope that reads no storage needs neither.</remarks>
internal sealed class StorageClearsScopeCache
{
    private const ulong NoPoisonedClear = ulong.MaxValue;

    private ConcurrentDictionary<ValueHash256, bool>? _hasAnyClear;
    private ConcurrentDictionary<ValueHash256, PoisonProbe>? _poisonedAbove;

    public bool HasAnyClearUpTo(in ValueHash256 addrHash, scoped ReadOnlySpan<byte> accountKey, StorageClearStore clears, ulong block)
    {
        ConcurrentDictionary<ValueHash256, bool> hasAnyClear = LazyInitializer.EnsureInitialized(ref _hasAnyClear, static () => new());
        if (hasAnyClear.TryGetValue(addrHash, out bool hasAny)) return hasAny;

        hasAny = clears.HasClearInRange(accountKey, 0, block);
        hasAnyClear.TryAdd(addrHash, hasAny);
        return hasAny;
    }

    public bool TryGetPoisonedClearAbove(in ValueHash256 addrHash, StorageClearStore clears, HistoryAvailability availability, ulong block, out ulong clearBlock)
    {
        ConcurrentDictionary<ValueHash256, PoisonProbe> poisonedAbove = LazyInitializer.EnsureInitialized(ref _poisonedAbove, static () => new());
        if (poisonedAbove.TryGetValue(addrHash, out PoisonProbe cached)
            && (cached.ClearBlock != NoPoisonedClear || !availability.HasCapturedSince(cached.Generation)))
        {
            clearBlock = cached.ClearBlock;
            return clearBlock != NoPoisonedClear;
        }

        long generation = availability.CaptureGeneration;
        clearBlock = clears.TryGetPoisonedClearAbove(addrHash.Bytes, block, out ulong found) ? found : NoPoisonedClear;
        poisonedAbove[addrHash] = new PoisonProbe(clearBlock, generation);
        return clearBlock != NoPoisonedClear;
    }

    private readonly record struct PoisonProbe(ulong ClearBlock, long Generation);
}
