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
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Benchmark
{
    public class UInt256ToHexStringBenchmark
    {
        private UInt256[] _scenarios = new UInt256[4];

        [Params(0, 1, 2, 3)]
        public int ScenarioIndex { get; set; }

        public UInt256ToHexStringBenchmark()
        {
            var a = new UInt256(Bytes.FromHexString("0xA0A1A2A3A4A5A6A7B0B1B2B3B4B5B6B7C0C1C2C3C4C5C6C7D0D1D2D3D4D5D6D7").AsSpan());
            var b = new UInt256(Bytes.FromHexString("0x0000000000000000000000000000000000000000000000000000000000000000").AsSpan());
            var c = new UInt256(Bytes.FromHexString("0x0000000000000000000000000000000000000000000000000000000000000001").AsSpan());
            var d = new UInt256(Bytes.FromHexString("0x0000000000000000000000000000000000000000000000000000000000000aaa").AsSpan());

            _scenarios[0] = a;
            _scenarios[1] = b;
            _scenarios[2] = c;
            _scenarios[3] = d;
        }

        [GlobalSetup]
        public void Setup()
        {
            int scenarioIndex = ScenarioIndex;
            for (int i = 0; i < _scenarios.Length; i++)
            {
                ScenarioIndex = i;
                string resultCurrent = Current();
                string resultImproved = Improved();

                Console.WriteLine($"{resultCurrent} vs {resultImproved}");
                if (resultCurrent != resultImproved)
                {
                    throw new InvalidBenchmarkDeclarationException($"{resultCurrent} vs {resultImproved}");
                }
            }

            ScenarioIndex = scenarioIndex;
        }

        [Benchmark]
        public string Improved()
        {
            return _scenarios[ScenarioIndex].ToHexString(true);
        }

        [Benchmark(Baseline = true)]
        public string Current()
        {
            return string.Concat("0x", _scenarios[ScenarioIndex].ToString("x").TrimStart('0'));
        }
    }
}
