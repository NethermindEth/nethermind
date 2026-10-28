// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;
using Nethermind.Evm.CodeAnalysis;

namespace Nethermind.Evm;

/// <summary>The zkVM guest's per-block <see cref="ICodeCache"/>: a plain map from code hash to code.</summary>
/// <remarks>
/// <see cref="StaticCodeCache"/> is built for concurrent readers and bounded memory, so every probe reads a seqlock
/// header and every hit stamps a clock, several times the cost of a map lookup on the guest. A block's code is bounded
/// by its witness and its gas (CREATE/CREATE2 deployments and EIP-7702 delegations also land here), and the guest
/// runs one thread, so nothing here needs either.
/// <para>
/// An open-addressed table indexed by the low word of the code hash: a keccak output is already uniform, and hashing
/// it again with 64-bit multiplies, as a general map does, costs the 32-bit guests more than the probe itself.
/// </para>
/// </remarks>
internal sealed class GuestCodeCache(int capacity) : ICodeCache
{
    private Entry[] _entries = new Entry[SizeFor(capacity)];
    private int _count;

    public CodeInfo? Get(in ValueHash256 codeHash)
    {
        Entry[] entries = _entries;
        int mask = entries.Length - 1;
        for (int i = Slot(in codeHash) & mask; ; i = (i + 1) & mask)
        {
            ref Entry entry = ref entries[i];
            if (entry.Code is null || entry.Hash.Equals(in codeHash)) return entry.Code;
        }
    }

    public void Set(in ValueHash256 codeHash, CodeInfo codeInfo)
    {
        // CacheCodeInfoRepository's memo matches on the stamped hash, as it does for StaticCodeCache.
        codeInfo.StampCodeHash(in codeHash);
        if (!Insert(_entries, in codeHash, codeInfo) && ++_count * 2 > _entries.Length) Grow();
    }

    public void Clear()
    {
        Array.Clear(_entries);
        _count = 0;
    }

    /// <summary>Stores <paramref name="codeInfo"/> under <paramref name="codeHash"/>; returns whether it replaced an entry.</summary>
    private static bool Insert(Entry[] entries, in ValueHash256 codeHash, CodeInfo codeInfo)
    {
        int mask = entries.Length - 1;
        for (int i = Slot(in codeHash) & mask; ; i = (i + 1) & mask)
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
        foreach (ref Entry entry in _entries.AsSpan())
        {
            if (entry.Code is not null) Insert(entries, in entry.Hash, entry.Code);
        }

        _entries = entries;
    }

    private static int SizeFor(int capacity) => (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(capacity, 4) * 2);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Slot(in ValueHash256 codeHash) => (int)Unsafe.As<ValueHash256, uint>(ref Unsafe.AsRef(in codeHash));

    private struct Entry
    {
        public ValueHash256 Hash;
        public CodeInfo? Code;
    }
}
