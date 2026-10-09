// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.State.Proofs;
using Nethermind.Trie;

namespace Nethermind.Benchmarks.State;

[MemoryDiagnoser]
public class AccountProofCollectorBenchmarks
{
    private readonly Address _address = new("0x0000000000000000000000000000000000000001");
    private UInt256[] _keys = null!;

    [Params(0, 1, 16, 256, 1000)]
    public int SlotCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _keys = new UInt256[SlotCount];
        for (int i = 0; i < _keys.Length; i++)
        {
            _keys[i] = (UInt256)(ulong)i;
        }
    }

    [Benchmark]
    public ReadOnlyMemory<ValueHash256> ArchiveInputs()
    {
        AccountProofCollector collector = new(_address, _keys);
        return collector.GetHashedStorageKeys();
    }

    [Benchmark]
    public bool TrieInputs()
    {
        AccountProofCollector collector = new(_address, _keys);
        TreePathContextWithStorage account = default;
        TreePathContextWithStorage storage = new() { Storage = Hash256.Zero };
        return collector.ShouldVisit(account, default) | collector.ShouldVisit(storage, default);
    }
}
