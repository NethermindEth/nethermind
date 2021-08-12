//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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
