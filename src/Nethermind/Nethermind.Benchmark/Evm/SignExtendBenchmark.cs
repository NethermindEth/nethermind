// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Nethermind.Core.Extensions;

namespace Nethermind.Benchmarks.Evm
{
    public class SignExtendBenchmark
    {
        [GlobalSetup]
        public void Setup()
        {
        }

        private byte[] a = new byte[32]
        {
            1, 17, 34, 50, 64, 78, 12, 56, 19, 12,
            120, 21, 123, 12, 76, 121, 1, 12, 23, 8,
            120, 21, 123, 12, 76, 121, 1, 12, 23, 8,
            120, 21
        };
        private byte[] b = new byte[32]
        {
            120, 21, 123, 12, 76, 121, 1, 12, 23, 8,
            77, 17, 34, 50, 64, 78, 12, 2, 19, 12,
            120, 21, 123, 12, 76, 121, 1, 12, 23, 8,
            55, 255
        };

        private byte[] c = new byte[32];

        [Benchmark(Baseline = true)]
        public void Current()
        {
            int position = 12;
            Span<byte> localB = this.b.AsSpan();
            BitArray bits1 = localB.ToBigEndianBitArray256();
            int bitPosition = Math.Max(0, 248 - 8 * (int)position);
            bool isSet = bits1[bitPosition];
            for (int i = 0; i < bitPosition; i++)
            {
                bits1[i] = isSet;
            }

            bits1.ToBytes().CopyTo(c.AsSpan());
        }

        private readonly byte[] BytesZero32 =
        {
            0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0
        };

        private readonly byte[] BytesMax32 =
        {
            255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 255
        };

        [Benchmark]
        public void Improved()
        {
            int position = 12;
            Span<byte> localB = b.AsSpan();
            sbyte sign = (sbyte)localB[position];

            if (sign < 0)
            {
                BytesZero32.AsSpan().Slice(0, position).CopyTo(localB.Slice(0, position));
            }
            else
            {
                BytesMax32.AsSpan().Slice(0, position).CopyTo(localB.Slice(0, position));
            }

            localB.CopyTo(c);
        }

        [Benchmark]
        public void Improved2()
        {
            int position = 12;
            Span<byte> localB = b.AsSpan();
            sbyte sign = (sbyte)localB[position];

            Span<byte> signBytes = sign < 0 ? BytesZero32.AsSpan() : BytesMax32.AsSpan();
            signBytes.Slice(0, position).CopyTo(b.Slice(0, position));

            localB.CopyTo(c);
        }
    }
}
