// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;

namespace Nethermind.Network.Benchmarks
{
    public class KdfDerivationBenchmarks
    {
        private static byte[] _z = Bytes.FromHexString("22ca1111ca383ef9d090ca567245eb72f80d8730fd4e1507e9a23bcdb3bb5a87");

        private OptimizedKdf _current = new OptimizedKdf();

        [Benchmark]
        public byte[] Current()
        {
            var result = _current.Derive(_z);
            return result;
        }
    }
}
