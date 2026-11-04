// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;
using Nethermind.Core.Crypto;

namespace Nethermind.State.Flat.History.Walk;

/// <summary>The last clear of each storage identity at a fixed replay boundary.</summary>
/// <remarks>Builds on first lookup. Clearing the pending source also remembers an initialized empty index.</remarks>
internal struct StorageClearIndex(IReadOnlyList<ClearRecord> clears, ulong atOrBefore)
{
    private IReadOnlyList<ClearRecord>? _pending = clears;
    private readonly ulong _atOrBefore = atOrBefore;
    private Dictionary<ValueHash256, ulong>? _latest;

    public ulong LatestClear(in ValueHash256 identity)
    {
        if (_pending is { } clears)
        {
            for (int i = 0; i < clears.Count; i++)
            {
                ClearRecord clear = clears[i];
                if (clear.Block > _atOrBefore) continue;

                _latest ??= [];
                ref ulong block = ref CollectionsMarshal.GetValueRefOrAddDefault(_latest, clear.Identity, out _);
                block = Math.Max(block, clear.Block);
            }

            _pending = null;
        }

        return _latest is { } latest && latest.TryGetValue(identity, out ulong found) ? found : 0;
    }
}
