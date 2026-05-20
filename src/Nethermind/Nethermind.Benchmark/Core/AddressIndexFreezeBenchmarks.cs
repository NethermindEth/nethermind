// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;

namespace Nethermind.Benchmarks.Core;

/// <summary>
/// Compares the current <c>AddressIndex</c> (plain <see cref="Dictionary{TKey,TValue}"/>) against
/// the proposed snapshot-to-<see cref="FrozenDictionary{TKey,TValue}"/> design from PR #11669.
///
/// Scenario: a "fat" ~300M-gas block of small transactions. With ~50k gas per ERC20-style
/// transfer we get on the order of 6k transactions touching 2-4 unique addresses each, so
/// ~8k unique addresses end up indexed. Per-block lookups come from two phases:
///   1. <c>Build.Fill</c> performs one <c>TryGet</c> per change event (~2-3 per account).
///   2. Per-tx validation runs <c>GetOrAdd</c> for the generated side (almost always a hit on
///      the suggested-side snapshot) and <c>TryGet</c> for structural checks.
/// We model that with N = 8000 inserts followed by M = 50000 lookups, all hits.
/// </summary>
[MemoryDiagnoser]
public class AddressIndexFreezeBenchmarks
{
    private const int UniqueAddresses = 8_000;
    private const int LookupCount = 50_000;

    private Address[] _addresses = null!;
    private Address[] _lookupOrder = null!;

    // Pre-built indexes for the lookup-only benchmarks (build cost excluded).
    private DictionaryAddressIndex _prebuiltDict = null!;
    private FrozenAddressIndex _prebuiltFrozen = null!;

    [GlobalSetup]
    public void Setup()
    {
        Random random = new(42);
        _addresses = new Address[UniqueAddresses];
        byte[] buf = new byte[20];
        for (int i = 0; i < UniqueAddresses; i++)
        {
            random.NextBytes(buf);
            _addresses[i] = new Address((byte[])buf.Clone());
        }

        // Pseudo-random lookup order; index modulo UniqueAddresses guarantees all hits and a
        // realistic cache-unfriendly access pattern.
        _lookupOrder = new Address[LookupCount];
        for (int i = 0; i < LookupCount; i++)
        {
            _lookupOrder[i] = _addresses[random.Next(UniqueAddresses)];
        }

        _prebuiltDict = new DictionaryAddressIndex();
        for (int i = 0; i < UniqueAddresses; i++) _prebuiltDict.GetOrAdd(_addresses[i]);

        _prebuiltFrozen = new FrozenAddressIndex();
        for (int i = 0; i < UniqueAddresses; i++) _prebuiltFrozen.GetOrAdd(_addresses[i]);
        _prebuiltFrozen.Freeze();
    }

    // --- Build phase only -----------------------------------------------------------------

    [Benchmark(Baseline = true)]
    public DictionaryAddressIndex Build_Dictionary()
    {
        DictionaryAddressIndex idx = new();
        Address[] addresses = _addresses;
        for (int i = 0; i < addresses.Length; i++) idx.GetOrAdd(addresses[i]);
        return idx;
    }

    [Benchmark]
    public FrozenAddressIndex Build_FrozenAndFreeze()
    {
        FrozenAddressIndex idx = new();
        Address[] addresses = _addresses;
        for (int i = 0; i < addresses.Length; i++) idx.GetOrAdd(addresses[i]);
        idx.Freeze();
        return idx;
    }

    // --- Lookup phase only (pre-built index, lookups dominate) ----------------------------

    [Benchmark]
    public int Lookups_Dictionary()
    {
        DictionaryAddressIndex idx = _prebuiltDict;
        Address[] keys = _lookupOrder;
        int sink = 0;
        for (int i = 0; i < keys.Length; i++)
        {
            if (idx.TryGet(keys[i], out int ordinal)) sink ^= ordinal;
        }
        return sink;
    }

    [Benchmark]
    public int Lookups_Frozen()
    {
        FrozenAddressIndex idx = _prebuiltFrozen;
        Address[] keys = _lookupOrder;
        int sink = 0;
        for (int i = 0; i < keys.Length; i++)
        {
            if (idx.TryGet(keys[i], out int ordinal)) sink ^= ordinal;
        }
        return sink;
    }

    // --- End-to-end: build + lookups (mirrors per-block usage) ----------------------------

    [Benchmark]
    public int EndToEnd_Dictionary()
    {
        DictionaryAddressIndex idx = new();
        Address[] addresses = _addresses;
        for (int i = 0; i < addresses.Length; i++) idx.GetOrAdd(addresses[i]);

        Address[] keys = _lookupOrder;
        int sink = 0;
        for (int i = 0; i < keys.Length; i++)
        {
            if (idx.TryGet(keys[i], out int ordinal)) sink ^= ordinal;
        }
        return sink;
    }

    [Benchmark]
    public int EndToEnd_FrozenAndFreeze()
    {
        FrozenAddressIndex idx = new();
        Address[] addresses = _addresses;
        for (int i = 0; i < addresses.Length; i++) idx.GetOrAdd(addresses[i]);
        idx.Freeze();

        Address[] keys = _lookupOrder;
        int sink = 0;
        for (int i = 0; i < keys.Length; i++)
        {
            if (idx.TryGet(keys[i], out int ordinal)) sink ^= ordinal;
        }
        return sink;
    }

    // --- The two AddressIndex shapes under test -------------------------------------------
    // Both mirror the public surface of BlockAccessListValidationIndex.AddressIndex; only the
    // backing store differs.

    public sealed class DictionaryAddressIndex
    {
        private readonly Dictionary<AddressAsKey, int> _ordinals = new(AddressAsKey.EqualityComparer);
        private readonly List<Address> _addresses = [];

        public int Count => _ordinals.Count;

        public int GetOrAdd(Address address)
        {
            ref int ordinal = ref CollectionsMarshal.GetValueRefOrAddDefault(_ordinals, address, out bool exists);
            if (!exists)
            {
                ordinal = _ordinals.Count - 1;
                _addresses.Add(address);
            }
            return ordinal;
        }

        public bool TryGet(Address address, out int ordinal)
            => _ordinals.TryGetValue(address, out ordinal);
    }

    public sealed class FrozenAddressIndex
    {
        private Dictionary<AddressAsKey, int> _mutable = new(AddressAsKey.EqualityComparer);
        private FrozenDictionary<AddressAsKey, int>? _frozen;
        private readonly List<Address> _addresses = [];

        public int Count => _addresses.Count;

        public void Freeze()
        {
            if (_frozen is not null) return;
            _frozen = _mutable.ToFrozenDictionary(AddressAsKey.EqualityComparer);
            _mutable = new Dictionary<AddressAsKey, int>(AddressAsKey.EqualityComparer);
        }

        public int GetOrAdd(Address address)
        {
            if (_frozen is { } frozen && frozen.TryGetValue(address, out int frozenOrdinal))
            {
                return frozenOrdinal;
            }

            ref int ordinal = ref CollectionsMarshal.GetValueRefOrAddDefault(_mutable, address, out bool exists);
            if (!exists)
            {
                ordinal = _addresses.Count;
                _addresses.Add(address);
            }
            return ordinal;
        }

        public bool TryGet(Address address, out int ordinal)
        {
            if (_frozen is { } frozen && frozen.TryGetValue(address, out ordinal))
            {
                return true;
            }
            return _mutable.TryGetValue(address, out ordinal);
        }
    }
}
