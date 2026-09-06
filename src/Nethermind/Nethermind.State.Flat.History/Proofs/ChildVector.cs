// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.State.Flat.History.Proofs;

internal sealed class ChildVector
{
    public const int SlotSize = Hash256.Size;
    private const int PoolCapacityPerThread = 512;
    [ThreadStatic]
    private static Stack<ChildVector>? t_pool;

    private readonly byte[] _bytes = new byte[BranchRlp.ChildCount * SlotSize];
    private readonly byte[] _lengths = new byte[BranchRlp.ChildCount];

    public static ChildVector Rent()
    {
        Stack<ChildVector>? pool = t_pool;
        return pool is { Count: > 0 } ? pool.Pop() : new ChildVector();
    }

    public static void Return(ChildVector vector)
    {
        vector.Clear();
        Stack<ChildVector> pool = t_pool ??= new Stack<ChildVector>(PoolCapacityPerThread);
        if (pool.Count < PoolCapacityPerThread) pool.Push(vector);
    }

    public ReadOnlySpan<byte> this[int index] => _bytes.AsSpan(index * SlotSize, _lengths[index]);

    public bool IsPresent(int index) => _lengths[index] != 0;

    public ushort Presence
    {
        get
        {
            ushort presence = 0;
            for (int index = 0; index < BranchRlp.ChildCount; index++)
            {
                if (_lengths[index] != 0) presence |= (ushort)(1 << index);
            }

            return presence;
        }
    }

    public void Set(int index, ReadOnlySpan<byte> reference)
    {
        if (reference.Length > SlotSize) throw new ArgumentOutOfRangeException(nameof(reference), reference.Length, "A child reference is a hash or an inline node shorter than a hash.");

        reference.CopyTo(_bytes.AsSpan(index * SlotSize));
        _lengths[index] = (byte)reference.Length;
    }

    public void SetHash(int index, in ValueHash256 hash)
    {
        hash.Bytes.CopyTo(_bytes.AsSpan(index * SlotSize));
        _lengths[index] = SlotSize;
    }

    public void Clear(int index) => _lengths[index] = 0;

    public void Clear() => Array.Clear(_lengths);

    public void CopyFrom(ChildVector other)
    {
        other._bytes.CopyTo(_bytes.AsSpan());
        other._lengths.CopyTo(_lengths.AsSpan());
    }

    public bool SameChild(int index, ChildVector other) =>
        _lengths[index] == other._lengths[index] && this[index].SequenceEqual(other[index]);

    public ushort ChangedSince(ChildVector? previous)
    {
        ushort changed = 0;
        for (int index = 0; index < BranchRlp.ChildCount; index++)
        {
            bool same = previous is not null && SameChild(index, previous);
            if (!same) changed |= (ushort)(1 << index);
        }

        return changed;
    }
}
