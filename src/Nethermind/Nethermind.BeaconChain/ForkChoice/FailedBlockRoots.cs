// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.ForkChoice;

/// <summary>Roots of blocks the importer refused for failing state transition or fork-choice validation, for readers on other threads (column gossip).</summary>
/// <remarks>
/// Bounded by <see cref="Capacity"/>: a full set drops its oldest root, so a peer that feeds invalid blocks cannot grow it. Roots at or
/// below the finalized slot are dropped by <see cref="Prune"/>; a sidecar of such a block is then judged as of any unseen block.
/// </remarks>
public sealed class FailedBlockRoots
{
    internal const int Capacity = 1024;

    private readonly Lock _lock = new();
    private readonly OrderedDictionary<Hash256, ulong> _slotsByRoot = [];

    internal int Count
    {
        get
        {
            lock (_lock)
            {
                return _slotsByRoot.Count;
            }
        }
    }

    /// <summary>Records that the block <paramref name="root"/> at <paramref name="slot"/> failed validation.</summary>
    public void Add(Hash256 root, ulong slot)
    {
        lock (_lock)
        {
            if (_slotsByRoot.ContainsKey(root))
            {
                return;
            }

            if (_slotsByRoot.Count >= Capacity)
            {
                _slotsByRoot.RemoveAt(0);
            }

            _slotsByRoot.Add(root, slot);
        }
    }

    public bool Contains(Hash256 root)
    {
        lock (_lock)
        {
            return _slotsByRoot.ContainsKey(root);
        }
    }

    /// <summary>Drops every root whose slot is at or below <paramref name="slot"/>.</summary>
    public void Prune(ulong slot)
    {
        lock (_lock)
        {
            for (int i = _slotsByRoot.Count - 1; i >= 0; i--)
            {
                if (_slotsByRoot.GetAt(i).Value <= slot)
                {
                    _slotsByRoot.RemoveAt(i);
                }
            }
        }
    }
}
