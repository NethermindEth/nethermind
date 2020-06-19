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
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Evm.Precompiles.Bls
{
    /// <summary>
    ///     Code adapted from ethereumJ (https://github.com/ethereum/ethereumj)
    /// </summary>
    public class G1AddPrecompile : IPrecompile
    {
        private static byte[] _zeroResult = new byte[64];

        public static IPrecompile Instance = new G1AddPrecompile();

        private G1AddPrecompile()
        {
        }

        public Address Address { get; } = Address.FromNumber(10);

        public long BaseGasCost(IReleaseSpec releaseSpec)
        {
            return 600L;
        }

        public long DataGasCost(byte[] inputData, IReleaseSpec releaseSpec)
        {
            return 0L;
        }

        private const int Len = 64;
        
        private readonly byte[] ZeroX16 = new byte[16];

        public (byte[], bool) Run(byte[] inputData)
        {
            inputData ??= Bytes.Empty;
            Span<byte> inputDataSpan = stackalloc byte[4 * Len];
            inputData.AsSpan(0, Math.Min(4 * Len, inputData.Length))
                .CopyTo(inputDataSpan.Slice(0, Math.Min(4 * Len, inputData.Length)));

            var x1 = inputDataSpan.Slice(0 * Len, Len);
            var y1 = inputDataSpan.Slice(1 * Len, Len);
            var x2 = inputDataSpan.Slice(2 * Len, Len);
            var y2 = inputDataSpan.Slice(3 * Len, Len);

            if (!Bytes.AreEqual(ZeroX16, x1.Slice(0, 16)) ||
                !Bytes.AreEqual(ZeroX16, y1.Slice(0, 16)) ||
                !Bytes.AreEqual(ZeroX16, x2.Slice(0, 16)) ||
                !Bytes.AreEqual(ZeroX16, y2.Slice(0, 16)))
            {
                return (Bytes.Empty, false);
            }
            
            var x1Int = new BigInteger(x1.Slice(16), true, true);
            var y1Int = new BigInteger(y1.Slice(16), true, true);
            var x2Int = new BigInteger(x2.Slice(16), true, true);
            var y2Int = new BigInteger(y2.Slice(16), true, true);

            var x1Copy = x1.Slice(16).ToArray();
            Bytes.ChangeEndianness8(x1Copy);
            
            MclBls12.G1 a = MclBls12.G1.Create(x1Int, y1Int);
            if (!a.IsValid())
            {
                return (Bytes.Empty, false);
            }

            MclBls12.G1 b = MclBls12.G1.Create(x2Int, y2Int);
            if (!b.IsValid())
            {
                return (Bytes.Empty, false);
            }

            MclBls12.G1 result = new MclBls12.G1();
            result.Add(a, b);

            byte[] encodedResult;
            if (result.IsZero())
            {
                encodedResult = _zeroResult;
            }
            else
            {
                string[] resultStrings = result.GetStr(0).Split(" ");
                BigInteger w1 = BigInteger.Parse(resultStrings[1]);
                BigInteger w2 = BigInteger.Parse(resultStrings[2]);
                encodedResult = EncodeResult(w1, w2);
            }

            return (encodedResult, true);
        }

        private static byte[] EncodeResult(BigInteger w1, BigInteger w2)
        {
            byte[] result = new byte[2 * Len];
            Span<byte> bytes = stackalloc byte[64];
            w1.TryWriteBytes(bytes, out int bytesWritten, true, true);
            bytes.Slice(0, bytesWritten).CopyTo(result.AsSpan(0 * Len + 16 + 48 - bytesWritten, bytesWritten));
            
            w2.TryWriteBytes(bytes, out bytesWritten, true, true);
            bytes.Slice(0, bytesWritten).CopyTo(result.AsSpan(1 * Len + 16 + 48 - bytesWritten, bytesWritten));
            
            return result;
        }
    }
}