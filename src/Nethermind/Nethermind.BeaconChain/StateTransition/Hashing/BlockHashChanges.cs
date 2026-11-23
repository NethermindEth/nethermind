// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.BeaconChain.StateTransition.Hashing;

/// <summary>Leaf indices written while processing one importer-owned Fulu block.</summary>
internal sealed class BlockHashChanges
{
    internal readonly Indices Validators = new();
    internal readonly Indices Balances = new();

    internal sealed class Indices
    {
        internal int[] Items = [];
        internal int Count;
        internal void Add(int index)
        {
            if (Count == Items.Length)
                Array.Resize(ref Items, Math.Max(8, Count * 2));
            Items[Count++] = index;
        }

        internal void Sort() => Array.Sort(Items, 0, Count);
    }
}
