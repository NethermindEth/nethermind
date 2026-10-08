// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

namespace Nethermind.Benchmarks.Evm;

public class KeyedNonceStorageSlotBenchmarks
{
    private readonly UInt256[] _keys = new UInt256[Eip8250Constants.MaxNonceKeys];
    private readonly UInt256[] _indices = new UInt256[Eip8250Constants.MaxNonceKeys];

    // 2 is the smallest set the AVX-512 gate admits, where an 8-lane permutation serves two keys and the
    // fixed per-call setup is least amortised, so it is the size that decides whether batching still pays.
    [Params(2, 8, Eip8250Constants.MaxNonceKeys)]
    public int Count { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        for (int i = 0; i < _keys.Length; i++)
        {
            _keys[i] = (UInt256)(i + 1);
        }
    }

    [Benchmark(Baseline = true)]
    public UInt256 Individual()
    {
        for (int i = 0; i < Count; i++)
        {
            _indices[i] = KeyedNonceManager.StorageSlot(Address.SystemUser, _keys[i]).Index;
        }

        return _indices[Count - 1];
    }

    [Benchmark]
    public UInt256 Batched()
    {
        KeyedNonceManager.StorageIndices(Address.SystemUser, _keys.AsSpan(0, Count), _indices);
        return _indices[Count - 1];
    }
}
