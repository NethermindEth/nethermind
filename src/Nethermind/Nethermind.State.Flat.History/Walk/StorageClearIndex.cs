// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;
using Nethermind.Core.Crypto;

namespace Nethermind.State.Flat.History.Walk;

/// <summary>The last clear of each storage identity at a fixed replay boundary.</summary>
/// <remarks>
/// Builds once, on the first lookup; the source list must not change afterwards.
/// Keep this mutable struct in a non-readonly field or local and do not copy it.
/// A block-0 clear cannot discard an unsigned write position, so zero also marks an empty inline slot.
/// </remarks>
internal struct StorageClearIndex(IReadOnlyList<ClearRecord> clears, ulong atOrBefore)
{
    private IReadOnlyList<ClearRecord>? _pending = clears;
    private readonly ulong _atOrBefore = atOrBefore;
    private ValueHash256 _singleIdentity;
    private ulong _singleBlock;
    private Dictionary<ValueHash256, ulong>? _latest;

    public ulong LatestClear(in ValueHash256 identity)
    {
        if (_pending is not null) Build();

        if (_latest is { } index) return index.TryGetValue(identity, out ulong found) ? found : 0;
        return _singleBlock != 0 && _singleIdentity == identity ? _singleBlock : 0;
    }

    private void Build()
    {
        IReadOnlyList<ClearRecord> clears = _pending!;
        for (int i = 0; i < clears.Count; i++)
        {
            ClearRecord clear = clears[i];
            if (clear.Block == 0 || clear.Block > _atOrBefore) continue;

            if (_latest is { } latest)
            {
                ref ulong block = ref CollectionsMarshal.GetValueRefOrAddDefault(latest, clear.Identity, out _);
                block = Math.Max(block, clear.Block);
            }
            else if (_singleBlock == 0)
            {
                _singleIdentity = clear.Identity;
                _singleBlock = clear.Block;
            }
            else if (_singleIdentity == clear.Identity)
            {
                _singleBlock = Math.Max(_singleBlock, clear.Block);
            }
            else
            {
                _latest = new Dictionary<ValueHash256, ulong>(2)
                {
                    [_singleIdentity] = _singleBlock,
                    [clear.Identity] = clear.Block
                };
            }
        }

        _pending = null;
    }
}
