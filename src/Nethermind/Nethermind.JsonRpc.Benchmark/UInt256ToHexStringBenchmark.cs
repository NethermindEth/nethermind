// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
            UInt256 a = new(Bytes.FromHexString("0xA0A1A2A3A4A5A6A7B0B1B2B3B4B5B6B7C0C1C2C3C4C5C6C7D0D1D2D3D4D5D6D7").AsSpan());
            UInt256 b = new(Bytes.FromHexString("0x0000000000000000000000000000000000000000000000000000000000000000").AsSpan());
            UInt256 c = new(Bytes.FromHexString("0x0000000000000000000000000000000000000000000000000000000000000001").AsSpan());
            UInt256 d = new(Bytes.FromHexString("0x0000000000000000000000000000000000000000000000000000000000000aaa").AsSpan());

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
        public string Improved() => _scenarios[ScenarioIndex].ToHexString(true);

        [Benchmark(Baseline = true)]
        public string Current() => _scenarios[ScenarioIndex].ToHexString(true);
    }
}
