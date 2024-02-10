// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;

namespace Nethermind.Benchmarks.Core;

public class BytesShiftLeft4Benchmarks
{
    [Params(1, 2, 3, 4, 25, 31, 1024)]
    public int Length { get; set; }

    private byte[] _bytes;

    [GlobalSetup]
    public void Setup()
    {
        _bytes = new byte[Length];
        for (int i = 0; i < Length; i++)
        {
            _bytes[i] = (byte)(i % 16);
        }
    }

    [Benchmark]
    public void ShiftLeft4()
    {
        Nethermind.Core.Extensions.Bytes.ShiftLeft4(_bytes);
    }
}
