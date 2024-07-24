// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using Nethermind.Core.Extensions;

namespace Nethermind.Benchmarks.Core
{
    public class FromHexBenchmarks
    {
        private byte[] _hex;
        private byte[] _bytes;

        [Params(32, 64, 128, 256, 512, 1024)]
        public int ByteLength;

        [GlobalSetup]
        public void Setup()
        {
            _hex = new byte[ByteLength * 2];
            _bytes = new byte[ByteLength];

            for (int i = 0; i < _bytes.Length; i++)
            {
                _bytes[i] = (byte)i;
            }

            Bytes.OutputBytesToByteHex(_bytes, _hex, extraNibble: false);
        }

        [Benchmark(Baseline = true)]
        public bool Scalar()
        {
            return HexConverter.TryDecodeFromUtf8_Scalar(_hex, _bytes, isOdd: false);
        }

        [Benchmark]
        public bool Vector128()
        {
            return HexConverter.TryDecodeFromUtf8_Vector128(_hex, _bytes);
        }

        [Benchmark]
        public bool Vector256()
        {
            return HexConverter.TryDecodeFromUtf8_Vector256(_hex, _bytes);
        }

        [Benchmark]
        public bool Vector512()
        {
            return HexConverter.TryDecodeFromUtf8_Vector512(_hex, _bytes);
        }
    }
}
