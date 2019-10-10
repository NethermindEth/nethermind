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

using System;
using System.Collections.Generic;
using System.Threading;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Perfshop
{
    [MemoryDiagnoser]
    [DisassemblyDiagnoser(printAsm: true)]
    [CoreJob(baseline: true)]
    public class BloomsBenchmark
    {
        private static Random random = new Random();

        private const ulong number = 1230123812841984UL;

        private List<List<byte[]>> _data = new List<List<byte[]>>();

        private List<(List<byte[]>, Bloom)> _dataWithBlooms = new List<(List<byte[]>, Bloom)>();

        [GlobalSetup]
        public void GlobalSetup()
        {
            for (int i = 0; i < 1000; i++)
            {
                Console.WriteLine(i);
                var nested = new List<byte[]>();
                _data.Add(nested);
                Bloom bloom = new Bloom();
                _dataWithBlooms.Add((nested, bloom));
                for (int j = 0; j < 1000; j++)
                {
                    byte[] bytes = new byte[32];
                    random.NextBytes(bytes);
                    bloom.Set(bytes);
                    nested.Add(bytes);
                }
            }
        }

        [Benchmark(Baseline = true)]
        public bool Search()
        {
            byte[] searchedFor = new byte[32];
            searchedFor[5] = 1;

            foreach (List<byte[]> byteses in _data)
            {
                foreach (byte[] bytes in byteses)
                {
                    Thread.Sleep(10);
                    if (Bytes.AreEqual(searchedFor, bytes))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        [Benchmark]
        public bool SearchWithBlooms()
        {
            byte[] searchedFor = new byte[32];
            searchedFor[5] = 1;

            foreach ((List<byte[]> Data, Bloom Bloom) dataWithBloom in _dataWithBlooms)
            {
                if (!dataWithBloom.Bloom.Matches(searchedFor))
                {
                    continue;
                }

                foreach (byte[] bytes in dataWithBloom.Data)
                {
                    if (Bytes.AreEqual(searchedFor, bytes))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}