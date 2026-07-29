// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;

namespace Nethermind.Benchmarks.Core
{
    /// <summary>EIP-8297 node-hash inputs, often with one empty 32-byte half.</summary>
    public class Blake3PairBenchmarks
    {
        private readonly byte[] _pair = new byte[64];
        private readonly byte[] _output = new byte[32];

        [Params("none", "low", "high")]
        public string ZeroHalf { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            new Random(42).NextBytes(_pair);
            if (ZeroHalf == "low") _pair.AsSpan(0, 32).Clear();
            if (ZeroHalf == "high") _pair.AsSpan(32, 32).Clear();
        }

        [Benchmark(Baseline = true)]
        public void Native() => global::Blake3.Hasher.Hash(_pair, _output);

        [Benchmark]
        public void Managed() => Blake3Managed.Hash(_pair, _output);

        [Benchmark]
        public void ManagedPair() => Blake3Managed.HashPair(_pair.AsSpan(0, 32), _pair.AsSpan(32, 32), _output);
    }

    [MemoryDiagnoser]
    public class Blake3WideFoldBenchmarks
    {
        public enum FoldShape
        {
            Empty,
            ClusteredPair,
            OppositeHalves,
            Alternating,
            Dense,
        }

        private const int PrefixLength = 4;
        private readonly byte[] _buffer = new byte[PrefixLength + 16 * ValueHash256.Length + 7];
        private readonly ValueHash256[] _sources = new ValueHash256[16];
        private int _compactLength;
        private int _presenceMask;

        [Params(8, 16)]
        public int Width { get; set; }

        [Params(FoldShape.Empty, FoldShape.ClusteredPair, FoldShape.OppositeHalves, FoldShape.Alternating, FoldShape.Dense)]
        public FoldShape Shape { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            _presenceMask = (Width, Shape) switch
            {
                (_, FoldShape.Empty) => 0,
                (_, FoldShape.ClusteredPair) => 0x0003,
                (8, FoldShape.OppositeHalves) => 0x0081,
                (16, FoldShape.OppositeHalves) => 0x8001,
                (8, FoldShape.Alternating) => 0x0055,
                (16, FoldShape.Alternating) => 0x5555,
                (8, FoldShape.Dense) => 0x00FF,
                _ => 0xFFFF,
            };

            _buffer.AsSpan().Fill(0xA5);
            _compactLength = 0;
            for (int source = 0; source < _sources.Length; source++)
            {
                byte[] bytes = new byte[ValueHash256.Length];
                for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(source + 17 * i + 1);
                bool present = source < Width && (_presenceMask & (1 << source)) != 0;
                _sources[source] = present ? new ValueHash256(bytes) : default;
                if (present)
                {
                    bytes.CopyTo(_buffer.AsSpan(PrefixLength + _compactLength));
                    _compactLength += bytes.Length;
                }
            }
        }

        [Benchmark(Baseline = true)]
        public ValueHash256 Scalar() => Width == 8 ? FoldEightScalar() : FoldSixteenScalar();

        [Benchmark]
        public ValueHash256 Compact() => Width == 8
            ? Blake3Hash.FoldEight(_buffer.AsSpan(PrefixLength, _compactLength), (byte)_presenceMask)
            : Blake3Hash.FoldSixteen(_buffer.AsSpan(PrefixLength, _compactLength), (ushort)_presenceMask);

        private ValueHash256 FoldEightScalar()
        {
            ValueHash256 a0 = Blake3Hash.HashPairOrZero(_sources[0], _sources[1]);
            ValueHash256 a1 = Blake3Hash.HashPairOrZero(_sources[2], _sources[3]);
            ValueHash256 a2 = Blake3Hash.HashPairOrZero(_sources[4], _sources[5]);
            ValueHash256 a3 = Blake3Hash.HashPairOrZero(_sources[6], _sources[7]);
            ValueHash256 b0 = Blake3Hash.HashPairOrZero(a0, a1);
            ValueHash256 b1 = Blake3Hash.HashPairOrZero(a2, a3);
            return Blake3Hash.HashPairOrZero(b0, b1);
        }

        private ValueHash256 FoldSixteenScalar()
        {
            ValueHash256 a0 = Blake3Hash.HashPairOrZero(_sources[0], _sources[1]);
            ValueHash256 a1 = Blake3Hash.HashPairOrZero(_sources[2], _sources[3]);
            ValueHash256 a2 = Blake3Hash.HashPairOrZero(_sources[4], _sources[5]);
            ValueHash256 a3 = Blake3Hash.HashPairOrZero(_sources[6], _sources[7]);
            ValueHash256 a4 = Blake3Hash.HashPairOrZero(_sources[8], _sources[9]);
            ValueHash256 a5 = Blake3Hash.HashPairOrZero(_sources[10], _sources[11]);
            ValueHash256 a6 = Blake3Hash.HashPairOrZero(_sources[12], _sources[13]);
            ValueHash256 a7 = Blake3Hash.HashPairOrZero(_sources[14], _sources[15]);
            ValueHash256 b0 = Blake3Hash.HashPairOrZero(a0, a1);
            ValueHash256 b1 = Blake3Hash.HashPairOrZero(a2, a3);
            ValueHash256 b2 = Blake3Hash.HashPairOrZero(a4, a5);
            ValueHash256 b3 = Blake3Hash.HashPairOrZero(a6, a7);
            ValueHash256 c0 = Blake3Hash.HashPairOrZero(b0, b1);
            ValueHash256 c1 = Blake3Hash.HashPairOrZero(b2, b3);
            return Blake3Hash.HashPairOrZero(c0, c1);
        }
    }
}
