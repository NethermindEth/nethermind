// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm.CodeAnalysis;

namespace Nethermind.Evm;

/// <summary>The zkVM guest's per-block <see cref="ICodeCache"/>: a plain map from code hash to code.</summary>
/// <remarks>
/// <see cref="StaticCodeCache"/> is built for concurrent readers and bounded memory, so every probe reads a seqlock
/// header and every hit stamps a clock, several times the cost of a map lookup on the guest. A block's code is bounded
/// by its witness and its gas (CREATE/CREATE2 deployments and EIP-7702 delegations also land here), and the guest
/// runs one thread, so nothing here needs either.
/// <para>
/// An open-addressed table indexed by a multiply-shift hash of the code hash's two low words: hashing all of it with
/// 64-bit multiplies, as a general map does, costs the 32-bit guests more than the probe itself. Code hashes can be
/// chosen, so the multipliers come from the per-run hash seed, as the general map's hash does.
/// </para>
/// </remarks>
internal sealed class GuestCodeCache(int capacity) : ICodeCache
{
    private readonly uint _key0 = DeriveKey(0);
    private readonly uint _key1 = DeriveKey(1);
    private Entry[] _entries = new Entry[SizeFor(capacity)];
    private int _shift = ShiftFor(SizeFor(capacity));
    private int _count;

    public CodeInfo? Get(in ValueHash256 codeHash)
    {
        Entry[] entries = _entries;
        int mask = entries.Length - 1;
        for (int i = Slot(in codeHash, _shift); ; i = (i + 1) & mask)
        {
            ref Entry entry = ref entries[i];
            if (entry.Code is null || entry.Hash.Equals(in codeHash)) return entry.Code;
        }
    }

    public void Set(in ValueHash256 codeHash, CodeInfo codeInfo)
    {
        // CacheCodeInfoRepository's memo matches on the stamped hash, as it does for StaticCodeCache.
        codeInfo.StampCodeHash(in codeHash);
        if (!Insert(_entries, _shift, in codeHash, codeInfo) && ++_count * 2 > _entries.Length) Grow();
    }

    public void Clear()
    {
        Array.Clear(_entries);
        _count = 0;
    }

    /// <summary>Stores <paramref name="codeInfo"/> under <paramref name="codeHash"/>; returns whether it replaced an entry.</summary>
    private bool Insert(Entry[] entries, int shift, in ValueHash256 codeHash, CodeInfo codeInfo)
    {
        int mask = entries.Length - 1;
        for (int i = Slot(in codeHash, shift); ; i = (i + 1) & mask)
        {
            ref Entry entry = ref entries[i];
            if (entry.Code is null)
            {
                entry.Hash = codeHash;
                entry.Code = codeInfo;
                return false;
            }

            if (entry.Hash.Equals(in codeHash))
            {
                entry.Code = codeInfo;
                return true;
            }
        }
    }

    private void Grow()
    {
        Entry[] entries = new Entry[_entries.Length * 2];
        int shift = _shift - 1;
        foreach (ref Entry entry in _entries.AsSpan())
        {
            if (entry.Code is not null) Insert(entries, shift, in entry.Hash, entry.Code);
        }

        _entries = entries;
        _shift = shift;
    }

    private static int SizeFor(int capacity) => (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(capacity, 4) * 2);

    /// <summary>Keeps the top bits of a 32-bit product, so a table of 2^k entries shifts by 32 - k.</summary>
    private static int ShiftFor(int length) => BitOperations.LeadingZeroCount((uint)length) + 1;

    private static uint DeriveKey(byte lane)
    {
        ReadOnlySpan<byte> input = [lane];
        return (uint)input.FastHash() | 1;
    }

    /// <summary>The bucket <paramref name="codeHash"/> starts probing from in the current table.</summary>
    internal int Slot(in ValueHash256 codeHash) => Slot(in codeHash, _shift);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Slot(in ValueHash256 codeHash, int shift)
    {
        ref uint words = ref Unsafe.As<ValueHash256, uint>(ref Unsafe.AsRef(in codeHash));
        return (int)((words * _key0 + Unsafe.Add(ref words, 1) * _key1) >> shift);
    }

    private struct Entry
    {
        public ValueHash256 Hash;
        public CodeInfo? Code;
    }
}
