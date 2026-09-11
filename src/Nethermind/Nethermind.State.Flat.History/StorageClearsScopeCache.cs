// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Core.Crypto;

namespace Nethermind.State.Flat.History;

/// <summary>
/// Per-scope memo of "does this account have any self-destruct marker at or before the scope's block".
/// </summary>
internal sealed class StorageClearsScopeCache
{
    private const ulong NoPoisonedClear = ulong.MaxValue;

    private readonly ConcurrentDictionary<ValueHash256, bool> _hasAnyClear = new();
    private readonly ConcurrentDictionary<ValueHash256, ulong> _poisonedAbove = new();

    public bool HasAnyClearUpTo(in ValueHash256 addrHash, scoped ReadOnlySpan<byte> accountKey, StorageClearStore clears, ulong block)
    {
        if (_hasAnyClear.TryGetValue(addrHash, out bool hasAny)) return hasAny;

        hasAny = clears.HasClearInRange(accountKey, 0, block);
        _hasAnyClear.TryAdd(addrHash, hasAny);
        return hasAny;
    }

    public bool TryGetPoisonedClearAbove(in ValueHash256 addrHash, StorageClearStore clears, ulong block, out ulong clearBlock)
    {
        if (_poisonedAbove.TryGetValue(addrHash, out clearBlock)) return clearBlock != NoPoisonedClear;

        clearBlock = clears.TryGetPoisonedClearAbove(addrHash.Bytes, block, out ulong found) ? found : NoPoisonedClear;
        _poisonedAbove.TryAdd(addrHash, clearBlock);
        return clearBlock != NoPoisonedClear;
    }
}
