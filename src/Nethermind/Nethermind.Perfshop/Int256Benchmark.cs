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
using System.Numerics;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Perfshop
{
    [MemoryDiagnoser]
    [DisassemblyDiagnoser]
    public class Int256Benchmark
    {
        public struct InputStruct
        {
            public InputStruct(byte[] a, byte[] b)
            {
                A = a;
                B = b;
            }
            
            public byte[] A;
            public byte[] B;

            public override string ToString()
            {
                return $"{A.ToHexString()} + {B.ToHexString()}";
            }
        }
        
        public InputStruct[] Scenarios => new[]
        {
            new InputStruct(Bytes.FromHexString("0x0000000000000000000000000000000000000000000000000000000000000000"), Bytes.FromHexString("0x0000000000000000000000000000000000000000000000000000000000000000")),
            new InputStruct(Bytes.FromHexString("0xFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF"), Bytes.FromHexString("0xFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF"))
        };

        private byte[] _result = new byte[32];
        
        // ReSharper disable once MemberCanBePrivate.Global
        // ReSharper disable once UnusedAutoPropertyAccessor.Global
        [ParamsSource(nameof(Scenarios))]
        public InputStruct Input { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            BigIntegerVersion();
            byte[] bigIntResult = _result;
            BigIntegerWithReverseVersion();
            byte[] bigIntWithReverseResult = _result;
            UInt256Version();
            byte[] uint256Result = _result;

            bigIntResult = bigIntResult.Slice(Math.Max(0, bigIntResult.Length - 32)).PadLeft(32);
            bigIntWithReverseResult = bigIntWithReverseResult.Slice(Math.Max(0, bigIntWithReverseResult.Length - 32)).PadLeft(32);
            
            if (!Bytes.AreEqual(bigIntResult, bigIntWithReverseResult))
            {
                throw new InvalidBenchmarkDeclarationException($"Reverse {bigIntResult.ToHexString().PadLeft(64, '0')} {bigIntWithReverseResult.ToHexString().PadLeft(64, '0')}");
            }
            
            if (!Bytes.AreEqual(bigIntResult, uint256Result))
            {
                throw new InvalidBenchmarkDeclarationException($"UInt256 {bigIntResult.ToHexString().PadLeft(64, '0')} {uint256Result.ToHexString()}");
            }
        }

        [Benchmark(Baseline = true)]
        public void BigIntegerVersion()
        {
            BigInteger a = new BigInteger(Input.A, true, true);
            BigInteger b = new BigInteger(Input.B, true, true);
            (a + b).TryWriteBytes(_result, out int bytesWritten, true, true);
        }
        
        [Benchmark]
        public void UInt256Version()
        {
            UInt256.CreateFromBigEndian(out UInt256 a, Input.A);
            UInt256.CreateFromBigEndian(out UInt256 b, Input.B);
            (a + b).ToBigEndian(_result);
        }
        
        [Benchmark]
        public void UInt256RefVersion()
        {
            UInt256.CreateFromBigEndian(out UInt256 a, Input.A);
            UInt256.CreateFromBigEndian(out UInt256 b, Input.B);
            UInt256.Add(out UInt256 res, ref a, ref b);
            res.ToBigEndian(_result);
        }

        [Benchmark]
        public void BigIntegerWithReverseVersion()
        {
            Bytes.Avx2Reverse256InPlace(Input.A);
            Bytes.Avx2Reverse256InPlace(Input.B);
            BigInteger a = new BigInteger(Input.A, true);
            BigInteger b = new BigInteger(Input.B, true);
            (a + b).TryWriteBytes(_result, out int bytesWritten, true, true);
        }
    }
}