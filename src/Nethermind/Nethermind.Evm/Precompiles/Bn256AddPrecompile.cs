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
using System.Runtime.InteropServices;
using mcl;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto.ZkSnarks;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Evm.Precompiles
{
    /// <summary>
    ///     Code adapted from ethereumJ (https://github.com/ethereum/ethereumj)
    /// </summary>
    public class Bn256AddPrecompile : IPrecompile
    {
        public static IPrecompile Instance = new Bn256AddPrecompile();

        private Bn256AddPrecompile()
        {
        }

        public Address Address { get; } = Address.FromNumber(6);

        public long BaseGasCost(IReleaseSpec releaseSpec)
        {
            return releaseSpec.IsEip1108Enabled ? 150L : 500L;
        }

        public long DataGasCost(byte[] inputData, IReleaseSpec releaseSpec)
        {
            return 0L;
        }

        public (byte[], bool) Run(byte[] inputData)
        {
            // if bit-length(p) % 8 != 0:
            //   size = Fp::getByteSize()
            //   if p is zero:
            //     return [0] * size
            //   else:
            //     s = x.serialize()
            //     # x in Fp2 is odd <=> x.a is odd
            //     if y is odd:
            //       s[byte-length(s) - 1] |= 0x80
            //     return s
            // else:
            //   size = Fp::getByteSize() + 1
            //   if p is zero:
            //     return [0] * size
            //   else:
            //     s = x.serialize()
            //     if y is odd:
            //       return 2:s
            //     else:
            //       return 3:s

            Metrics.Bn128AddPrecompile++;

            if (inputData == null)
            {
                inputData = Bytes.Empty;
            }

            if (inputData.Length < 128)
            {
                inputData = inputData.PadRight(128);
            }

            byte[] x1 = inputData.Slice(0, 32);
            byte[] y1 = inputData.Slice(32, 32);
            byte[] oneBytes = new byte[32];
            oneBytes[31] = 1;

            byte[] x2 = inputData.Slice(64, 32);
            byte[] y2 = inputData.Slice(96, 32);


            Span<byte> something = new byte[96];
            x1.AsSpan().CopyTo(something.Slice(0, 32));
            y1.AsSpan().CopyTo(something.Slice(32, 32));
            oneBytes.AsSpan().CopyTo(something.Slice(64, 32));
            var one = Fp.One;

            var a = new Fp(x1);
            var b = new Fp(y1);
            var c = new Fp(oneBytes);
            bool isValid = b.IsValid();

            BN256.init();
            

            BN256.Fp ap = new BN256.Fp();
            BN256.Fp bp = new BN256.Fp();
            BN256.Fp cp = new BN256.Fp();
            ap.SetStr(a.ToString(), 0);
            bp.SetStr(b.ToString(), 0);
            cp.SetStr(c.ToString(), 0);

            byte[] oa = new byte[32];
            byte[] ob = new byte[32];
            byte[] oc = new byte[32];

            // ap.Serialize(oa, 0);
            // bp.Serialize(ob, 0);
            // cp.Serialize(oc, 31);

            // ap.Mul(cp, cp);
            
            string ast = ap.GetStr(0);
            string bs = bp.GetStr(0);
            string cs = cp.GetStr(0);
            
            BN256.G1 gaaa = new BN256.G1();
            // gaaa.setStr($"1 {ast} {bs}", 0);
            gaaa.setStr($"2 {ast}", 0);
            for (int i = 0; i < 1024; i++)
            {
                try
                {
                    string aas = gaaa.GetStr(i);
                    Console.WriteLine(aas);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"{i} BAD");
                }
            }
            
            
            string g1s2 = gaaa.GetStr(0);
            
            gaaa.setStr($"3 {ast}", 0);
            string g1s3 = gaaa.GetStr(0);

            Bn128Fp p1 = Bn128Fp.Create(x1, y1);
            bool isOn = p1.IsOnCurve();
            
            if (p1 == null)
            {
                return (Bytes.Empty, false);
            }

            Bn128Fp p2 = Bn128Fp.Create(x2, y2);
            if (p2 == null)
            {
                return (Bytes.Empty, false);
            }

            Bn128Fp res = p1.Add(p2).ToEthNotation();

            return (EncodeResult(res.X.GetBytes(), res.Y.GetBytes()), true);
        }

        private static byte[] EncodeResult(byte[] w1, byte[] w2)
        {
            byte[] result = new byte[64];

            // TODO: do I need to strip leading zeros here? // probably not
            w1.AsSpan().WithoutLeadingZeros().CopyTo(result.AsSpan().Slice(32 - w1.Length, w1.Length));
            w2.AsSpan().WithoutLeadingZeros().CopyTo(result.AsSpan().Slice(64 - w2.Length, w2.Length));
            return result;
        }
    }
}