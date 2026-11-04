// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.State.Flat.History.Walk;

namespace Nethermind.Benchmarks.State;

[MemoryDiagnoser]
public class StorageClearIndexBenchmarks
{
    private const ulong Boundary = 128;
    private ClearRecord[] _clears = [];
    private ValueHash256[] _identities = [];

    [Params(0, 1, 2, 64)]
    public int ClearCount { get; set; }

    [Params(1, 1024)]
    public int SlotCount { get; set; }

    [Params(false, true)]
    public bool DistinctIdentities { get; set; }

    [Params(0UL, Boundary)]
    public ulong WrittenAt { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _clears = new ClearRecord[ClearCount];
        _identities = new ValueHash256[Math.Max(1, DistinctIdentities ? ClearCount : 1)];
        for (int i = 0; i < _identities.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(_identities[i].BytesAsSpan, i + 1);
        }

        for (int i = 0; i < _clears.Length; i++)
        {
            _clears[i] = new ClearRecord(_identities[DistinctIdentities ? i : 0], (ulong)(2 * i + 1));
        }
    }

    [Benchmark(Baseline = true)]
    public int ScanClearRecords()
    {
        IReadOnlyList<ClearRecord> clears = _clears;
        int retained = 0;
        for (int slot = 0; slot < SlotCount; slot++)
        {
            ValueHash256 identity = _identities[slot % _identities.Length];
            bool killed = false;
            for (int i = 0; i < clears.Count; i++)
            {
                ClearRecord clear = clears[i];
                if (clear.Identity == identity && clear.Block > WrittenAt && clear.Block <= Boundary)
                {
                    killed = true;
                    break;
                }
            }

            if (!killed) retained++;
        }

        return retained;
    }

    [Benchmark]
    public int ReuseLatestClear()
    {
        StorageClearIndex index = new(_clears, Boundary);
        int retained = 0;
        for (int slot = 0; slot < SlotCount; slot++)
        {
            ValueHash256 identity = _identities[slot % _identities.Length];
            if (WrittenAt >= index.LatestClear(identity)) retained++;
        }

        return retained;
    }
}
