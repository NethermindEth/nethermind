// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Core.Crypto;

namespace Nethermind.State.Flat.History;

/// <summary>
/// Per-scope memo of "does this account have any self-destruct marker at or before the scope's block".
/// </summary>
/// <remarks>
/// Almost no account ever self-destructs, yet every resolved historical slot read must rule a destruct out — an
/// iterator-backed range probe per slot. A scope is pinned to one block, so one existence probe per address answers
/// it for every slot the scope touches; only the rare account with markers still pays the exact per-slot range probe.
/// </remarks>
internal sealed class StorageClearsScopeCache
{
    private readonly ConcurrentDictionary<ValueHash256, bool> _hasAnyClear = new();

    public bool HasAnyClearUpTo(in ValueHash256 addrHash, scoped ReadOnlySpan<byte> accountKey, StorageClearStore clears, ulong block)
    {
        if (_hasAnyClear.TryGetValue(addrHash, out bool hasAny)) return hasAny;

        // An exclusive lower bound of 0 skips a marker exactly at block 0; genesis cannot self-destruct, so the
        // full [0, block] range is covered.
        hasAny = clears.HasClearInRange(accountKey, 0, block);
        _hasAnyClear.TryAdd(addrHash, hasAny);
        return hasAny;
    }
}
