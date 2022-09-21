//  Copyright (c) 2021 Demerzel Solutions Limited
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
//

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Core.Extensions;
using Nethermind.Trie;

namespace Nethermind.Benchmarks.Store;

// a coarse grained benchmark, showing the RLP -> HexPrefix -> combine paths -> HexPrefix -> RLP
[MemoryDiagnoser]
public class HexPrefixAgainstRawPathBenchmarks
{
    private const byte Prefix = 0;
    private byte[] _even = HexPrefix.Leaf(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }).ToBytes();
    private byte[] _odd = HexPrefix.Leaf(new byte[] { 1, 2, 3, 4, 5, 6, 7 }).ToBytes();

    [Benchmark]
    public byte[] RawPath()
    {
        // RLP copy
        byte[] even = _even.AsSpan().ToArray();

        // combine and it's ready
        return HexPrefix.RawPath.Combine(Prefix, even);
    }

    [Benchmark(Baseline = true)]
    public byte[] Current()
    {
        return HexPrefix.Leaf(Bytes.Concat(Prefix, HexPrefix.FromBytes(_even).Path)).ToBytes();
    }
}
