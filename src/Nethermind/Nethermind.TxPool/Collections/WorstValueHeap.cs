// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Nethermind.TxPool.Collections;

/// <summary>A max-heap of each bucket's worst value, indexed so any member can be removed or re-sifted in place.</summary>
/// <remarks>
/// The heap's arrays only grow, so steady-state churn allocates nothing. <see cref="Max"/> is well defined because the
/// comparer is a strict total order (the pool comparer ends in a hash tie-break). A member's ordering inputs may change
/// only immediately before <see cref="Update"/>.
/// </remarks>
internal sealed class WorstValueHeap<TValue, TKey>(IComparer<TValue> comparer) where TValue : notnull
{
    private readonly Dictionary<TValue, int> _slots = [with(ReferenceComparer.Instance)];
    private readonly List<(TValue Value, TKey Key)> _heap = [];

    public int Count => _heap.Count;

    /// <summary>The worst value, or <see langword="default"/> when empty, as <see cref="SortedSet{T}.Max"/> returns.</summary>
    public KeyValuePair<TValue, TKey> Max => Count == 0 ? default : new(_heap[0].Value, _heap[0].Key);

    public bool Contains(TValue value) => _slots.ContainsKey(value);

    public bool Add(TValue value, TKey key)
    {
        if (!_slots.TryAdd(value, Count)) return false;

        _heap.Add((value, key));
        SiftUp(Count - 1);
        return true;
    }

    public bool Remove(TValue value)
    {
        if (!_slots.Remove(value, out int slot)) return false;

        int last = Count - 1;
        if (slot != last) Move(last, slot);
        _heap.RemoveAt(last);
        if (slot != last && !SiftUp(slot)) SiftDown(slot);
        return true;
    }

    /// <summary>Restores heap order after <paramref name="value"/>'s ordering inputs changed in place.</summary>
    public void Update(TValue value)
    {
        if (_slots.TryGetValue(value, out int slot) && !SiftUp(slot)) SiftDown(slot);
    }

    private bool SiftUp(int slot)
    {
        int start = slot;
        (TValue Value, TKey Key) item = _heap[slot];
        while (slot > 0)
        {
            int parent = (slot - 1) >> 1;
            if (comparer.Compare(item.Value, _heap[parent].Value) <= 0) break;

            Move(parent, slot);
            slot = parent;
        }

        // An unmoved item's slot is already recorded, so skip the dictionary write.
        if (slot == start) return false;

        Place(item, slot);
        return true;
    }

    private void SiftDown(int slot)
    {
        int start = slot;
        (TValue Value, TKey Key) item = _heap[slot];
        while (true)
        {
            int child = 2 * slot + 1;
            if (child >= Count) break;
            if (child + 1 < Count && comparer.Compare(_heap[child + 1].Value, _heap[child].Value) > 0) child++;
            if (comparer.Compare(item.Value, _heap[child].Value) >= 0) break;

            Move(child, slot);
            slot = child;
        }

        if (slot != start) Place(item, slot);
    }

    private void Move(int from, int to) => Place(_heap[from], to);

    private void Place((TValue Value, TKey Key) item, int slot)
    {
        _heap[slot] = item;
        _slots[item.Value] = slot;
    }

    private sealed class ReferenceComparer : IEqualityComparer<TValue>
    {
        public static readonly ReferenceComparer Instance = new();

        public bool Equals(TValue? x, TValue? y) => ReferenceEquals(x, y);

        public int GetHashCode(TValue obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
