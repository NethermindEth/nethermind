/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Dirichlet.Benchmark
{
    [MemoryDiagnoser]
    [CoreJob(baseline: true)]
    public class UInt256ToBigEndianBenchmarks
    {
        private byte[] _address = new byte[20];
        private byte[] _stack = new byte[32];
        
        private UInt256[] _scenarios = new UInt256[3];

        [Params(0, 1, 2)]
        public int ScenarioIndex { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            UInt256.CreateFromBigEndian(out UInt256 a, Bytes.FromHexString("0xA0A1A2A3A4A5A6A7B0B1B2B3B4B5B6B7C0C1C2C3C4C5C6C7D0D1D2D3D4D5D6D7").AsSpan());
            UInt256.CreateFromBigEndian(out UInt256 b, Bytes.FromHexString("0x0000000000000000000000000000000000000000000000000000000000000000").AsSpan());
            UInt256.CreateFromBigEndian(out UInt256 c, Bytes.FromHexString("0xFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF").AsSpan());

            _scenarios[0] = a;
            _scenarios[1] = b;
            _scenarios[2] = c;
            
            Current();
            var current = _stack.Clone() as byte[];
            var currentAddress = _address.Clone() as byte[];
            Console.WriteLine($"Current: {current.ToHexString()} | {currentAddress.ToHexString()}");
            
            Improved();
            var improved = _stack.Clone() as byte[];
            var improvedAddress = _address.Clone() as byte[];
            Console.WriteLine($"Improved: {improved.ToHexString()} | {improvedAddress.ToHexString()}");
            
            if (!Bytes.AreEqual(current, improved))
            {
                throw new InvalidBenchmarkDeclarationException($"{current.ToHexString()} != {improved.ToHexString()}");
            }
            
            if (!Bytes.AreEqual(currentAddress, improvedAddress))
            {
                throw new InvalidBenchmarkDeclarationException($"{currentAddress.ToHexString()} != {improvedAddress.ToHexString()}");
            }
        }

        [Benchmark(Baseline = true)]
        public void Current()
        {
            _scenarios[ScenarioIndex].ToBigEndian(_address);
            _scenarios[ScenarioIndex].ToBigEndian(_stack);
        }

        [Benchmark]
        public void Improved()
        {
            _scenarios[ScenarioIndex].ToBigEndian(_address);
            _scenarios[ScenarioIndex].ToBigEndian(_stack);
        }
    }
}