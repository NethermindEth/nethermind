// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Benchmarks.Evm;

/// <summary>
/// The two lookups every CALL pays: "is this a precompile" against <see cref="FrozenSet{T}"/>, and, when
/// it is, fetching it from a <see cref="FrozenDictionary{TKey, TValue}"/> — against deriving the
/// precompile's number from the address and using it as an index.
/// </summary>
/// <remarks>
/// Precompile addresses are the first sixteen bytes zero and a small number in the last four, so the
/// number is two loads and a byte swap, and both lookups become an array index. The membership test is
/// the one that matters: it runs on every CALL, not only the ones that land on a precompile, which is
/// why the mix below is mostly ordinary addresses.
/// </remarks>
public class PrecompileLookupBenchmark
{
    private FrozenSet<AddressAsKey> _set = null!;
    private FrozenDictionary<AddressAsKey, object> _dictionary = null!;
    private ulong _mask;
    private object?[] _byIndex = null!;
    private Address[] _addresses = null!;

    [GlobalSetup]
    public void Setup()
    {
        Dictionary<AddressAsKey, object> entries = [];
        _byIndex = new object[0x101];
        for (int i = 1; i <= 0x11; i++)
        {
            Address address = Address.FromNumber((UInt256)(ulong)i);
            object value = new();
            entries[address] = value;
            _mask |= 1UL << i;
            _byIndex[i] = value;
        }

        // RIP-7212, the one precompile that sits outside the low run.
        Address p256 = Address.FromNumber((UInt256)0x100UL);
        object p256Value = new();
        entries[p256] = p256Value;
        _byIndex[0x100] = p256Value;

        _dictionary = entries.ToFrozenDictionary();
        _set = _dictionary.Keys.ToFrozenSet();

        // A realistic CALL mix: mostly ordinary contracts, which is what the membership test rejects.
        _addresses = new Address[16];
        for (int i = 0; i < _addresses.Length; i++)
        {
            _addresses[i] = i switch
            {
                3 => Address.FromNumber(UInt256.One),
                7 => Address.FromNumber((UInt256)2UL),
                11 => Address.FromNumber((UInt256)0x100UL),
                _ => OrdinaryAddress(i),
            };
        }
    }

    /// <summary>An address no precompile could occupy: its leading bytes are set.</summary>
    private static Address OrdinaryAddress(int seed)
    {
        byte[] bytes = new byte[Address.Size];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(seed * 31 + i * 7 + 1);
        return new Address(bytes);
    }

    /// <summary>The precompile's number, or negative if the address cannot be one.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int IndexOrNegative(Address address)
    {
        ref byte b = ref Unsafe.AsRef(in address.Bytes[0]);
        if ((Unsafe.ReadUnaligned<ulong>(ref b) | Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b, 8))) != 0)
        {
            return -1;
        }

        return (int)BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref b, 16)));
    }

    [Benchmark(Baseline = true)]
    public int Membership_FrozenSet()
    {
        int found = 0;
        foreach (Address address in _addresses)
        {
            if (_set.Contains(address)) found++;
        }

        return found;
    }

    [Benchmark]
    public int Membership_IndexAndMask()
    {
        int found = 0;
        foreach (Address address in _addresses)
        {
            int index = IndexOrNegative(address);
            if ((uint)index < 64 ? (_mask & (1UL << index)) != 0 : index == 0x100) found++;
        }

        return found;
    }

    [Benchmark]
    public int Lookup_FrozenDictionary()
    {
        int hit = 0;
        foreach (Address address in _addresses)
        {
            if (_set.Contains(address) && _dictionary[address] is not null) hit++;
        }

        return hit;
    }

    [Benchmark]
    public int Lookup_IndexAndArray()
    {
        int hit = 0;
        foreach (Address address in _addresses)
        {
            int index = IndexOrNegative(address);
            if ((uint)index < (uint)_byIndex.Length && _byIndex[index] is not null) hit++;
        }

        return hit;
    }
}
