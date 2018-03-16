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
using System.Numerics;
using Nethermind.Core.Extensions;

namespace Nethermind.Core.Crypto.ZkSnarks
{
    /// <summary>
    ///     Code adapted from ethereumJ (https://github.com/ethereum/ethereumj)
    /// </summary>
    public class Fp12 : QuadraticExtension<Fp6, Fp12>
    {
        static Fp12()
        {
            FieldParams<Fp12>.Zero = Zero;
            FieldParams<Fp12>.One = One;
        }

        public static Fp12 Zero = new Fp12(Fp6.Zero, Fp6.Zero);
        public static Fp12 One = new Fp12(Fp6.One, Fp6.Zero);
        
        public Fp12()
        {
        }
        
        public Fp12(Fp6 a, Fp6 b)
        {
            A = a;
            B = b;
        }
        
        public Fp12 MulBy024(Fp2 ell0, Fp2 ellVw, Fp2 ellVv) {

            Fp2 z0 = A.A;
            Fp2 z1 = A.B;
            Fp2 z2 = A.C;
            Fp2 z3 = B.A;
            Fp2 z4 = B.B;
            Fp2 z5 = B.C;

            Fp2 x0 = ell0;
            Fp2 x2 = ellVv;
            Fp2 x4 = ellVw;

            Fp2 d0 = z0.Mul(x0);
            Fp2 d2 = z2.Mul(x2);
            Fp2 d4 = z4.Mul(x4);
            Fp2 t2 = z0.Add(z4);
            Fp2 t1 = z0.Add(z2);
            Fp2 s0 = z1.Add(z3).Add(z5);

            // For z.a_.a_ = z0.
            Fp2 s1 = z1.Mul(x2);
            Fp2 t3 = s1.Add(d4);
            Fp2 t4 = Fp6.NonResidue.Mul(t3).Add(d0);
            z0 = t4;

            // For z.a_.b_ = z1
            t3 = z5.Mul(x4);
            s1 = s1.Add(t3);
            t3 = t3.Add(d2);
            t4 = Fp6.NonResidue.Mul(t3);
            t3 = z1.Mul(x0);
            s1 = s1.Add(t3);
            t4 = t4.Add(t3);
            z1 = t4;

            // For z.a_.c_ = z2
            Fp2 t0 = x0.Add(x2);
            t3 = t1.Mul(t0).Sub(d0).Sub(d2);
            t4 = z3.Mul(x4);
            s1 = s1.Add(t4);
            t3 = t3.Add(t4);

            // For z.b_.a_ = z3 (z3 needs z2)
            t0 = z2.Add(z4);
            z2 = t3;
            t1 = x2.Add(x4);
            t3 = t0.Mul(t1).Sub(d2).Sub(d4);
            t4 = Fp6.NonResidue.Mul(t3);
            t3 = z3.Mul(x0);
            s1 = s1.Add(t3);
            t4 = t4.Add(t3);
            z3 = t4;

            // For z.b_.b_ = z4
            t3 = z5.Mul(x2);
            s1 = s1.Add(t3);
            t4 = Fp6.NonResidue.Mul(t3);
            t0 = x0.Add(x4);
            t3 = t2.Mul(t0).Sub(d0).Sub(d4);
            t4 = t4.Add(t3);
            z4 = t4;

            // For z.b_.c_ = z5.
            t0 = x0.Add(x2).Add(x4);
            t3 = s0.Mul(t0).Sub(s1);
            z5 = t3;

            return new Fp12(new Fp6(z0, z1, z2), new Fp6(z3, z4, z5));
        }
        
        public Fp12 CyclotomicSquare()
        {
            Fp2 z0 = A.A;
            Fp2 z4 = A.B;
            Fp2 z3 = A.C;
            Fp2 z2 = B.A;
            Fp2 z1 = B.B;
            Fp2 z5 = B.C;

            // t0 + t1*y = (z0 + z1*y)^2 = a^2
            Fp2 tmp = z0.Mul(z1);
            Fp2 t0 = z0.Add(z1).Mul(z0.Add(Fp6.NonResidue.Mul(z1))).Sub(tmp).Sub(Fp6.NonResidue.Mul(tmp));
            Fp2 t1 = tmp.Add(tmp);
            // t2 + t3*y = (z2 + z3*y)^2 = b^2
            tmp = z2.Mul(z3);
            Fp2 t2 = z2.Add(z3).Mul(z2.Add(Fp6.NonResidue.Mul(z3))).Sub(tmp).Sub(Fp6.NonResidue.Mul(tmp));
            Fp2 t3 = tmp.Add(tmp);
            // t4 + t5*y = (z4 + z5*y)^2 = c^2
            tmp = z4.Mul(z5);
            Fp2 t4 = z4.Add(z5).Mul(z4.Add(Fp6.NonResidue.Mul(z5))).Sub(tmp).Sub(Fp6.NonResidue.Mul(tmp));
            Fp2 t5 = tmp.Add(tmp);

            // for A

            // z0 = 3 * t0 - 2 * z0
            z0 = t0.Sub(z0);
            z0 = z0.Add(z0);
            z0 = z0.Add(t0);
            // z1 = 3 * t1 + 2 * z1
            z1 = t1.Add(z1);
            z1 = z1.Add(z1);
            z1 = z1.Add(t1);

            // for B

            // z2 = 3 * (xi * t5) + 2 * z2
            tmp = Fp6.NonResidue.Mul(t5);
            z2 = tmp.Add(z2);
            z2 = z2.Add(z2);
            z2 = z2.Add(tmp);

            // z3 = 3 * t4 - 2 * z3
            z3 = t4.Sub(z3);
            z3 = z3.Add(z3);
            z3 = z3.Add(t4);

            // for C

            // z4 = 3 * t2 - 2 * z4
            z4 = t2.Sub(z4);
            z4 = z4.Add(z4);
            z4 = z4.Add(t2);

            // z5 = 3 * t3 + 2 * z5
            z5 = t3.Add(z5);
            z5 = z5.Add(z5);
            z5 = z5.Add(t3);

            return new Fp12(new Fp6(z0, z4, z3), new Fp6(z2, z1, z5));
        }

        public Fp12 CyclotomicExp(BigInteger pow)
        {
            Fp12 res = FieldParams<Fp12>.One;

            for (int i = pow.BitLength() - 1; i >=0; i--) {
                res = res.CyclotomicSquare();

                if (pow.TestBit(i)) {
                    res = res.Mul(this);
                }
            }

            return res;
        }
        
        public Fp12 UnitaryInverse() {

            Fp6 ra = A;
            Fp6 rb = B.Negate();

            return new Fp12(ra, rb);
        }
        
        public Fp12 NegExp(BigInteger exp)
        {
            return CyclotomicExp(exp).UnitaryInverse();
        }

        public Fp12 FrobeniusMap(int power)
        {
            Fp6 ra = A.FrobeniusMap(power);
            Fp6 rb = B.FrobeniusMap(power).Mul(FrobeniusCoefficientsB[power % 12]);

            return new Fp12(ra, rb);
        }
        
        // https://github.com/scipr-lab/libff/blob/master/libff/algebra/curves/alt_bn128/alt_bn128_init.cpp
        public static readonly Fp2[] FrobeniusCoefficientsB =
        {
            new Fp2(
                BigInteger.One,
                BigInteger.Zero),

            // xiToPMinus1Over6 is ξ^((p-1)/6) where ξ = i+9.
            new Fp2(
                BigInteger.Parse("8376118865763821496583973867626364092589906065868298776909617916018768340080"),
                BigInteger.Parse("16469823323077808223889137241176536799009286646108169935659301613961712198316")),

            // xiToPSquaredMinus1Over6 is ξ^((1p²-1)/6) where ξ = i+9 (a cubic root of -1, mod p).
            new Fp2(
                BigInteger.Parse("21888242871839275220042445260109153167277707414472061641714758635765020556617"),
                BigInteger.Zero),

            new Fp2(
                BigInteger.Parse("11697423496358154304825782922584725312912383441159505038794027105778954184319"),
                BigInteger.Parse("303847389135065887422783454877609941456349188919719272345083954437860409601")),

            // xiToPSquaredMinus1Over3 is ξ^((p²-1)/3) where ξ = i+9.
            new Fp2(
                BigInteger.Parse("21888242871839275220042445260109153167277707414472061641714758635765020556616"),
                BigInteger.Zero),

            new Fp2(
                BigInteger.Parse("3321304630594332808241809054958361220322477375291206261884409189760185844239"),
                BigInteger.Parse("5722266937896532885780051958958348231143373700109372999374820235121374419868")),

            new Fp2(
                BigInteger.Parse("21888242871839275222246405745257275088696311157297823662689037894645226208582"),
                BigInteger.Zero),

            new Fp2(
                BigInteger.Parse("13512124006075453725662431877630910996106405091429524885779419978626457868503"),
                BigInteger.Parse("5418419548761466998357268504080738289687024511189653727029736280683514010267")),

            // xiTo2PSquaredMinus2Over3 is ξ^((2p²-2)/3) where ξ = i+9 (a cubic root of unity, mod p).
            new Fp2(
                BigInteger.Parse("2203960485148121921418603742825762020974279258880205651966"),
                BigInteger.Zero),

            new Fp2(
                BigInteger.Parse("10190819375481120917420622822672549775783927716138318623895010788866272024264"),
                BigInteger.Parse("21584395482704209334823622290379665147239961968378104390343953940207365798982")),

            new Fp2(
                BigInteger.Parse("2203960485148121921418603742825762020974279258880205651967"),
                BigInteger.Zero),

            new Fp2(
                BigInteger.Parse("18566938241244942414004596690298913868373833782006617400804628704885040364344"),
                BigInteger.Parse("16165975933942742336466353786298926857552937457188450663314217659523851788715"))
        };

        public override Fp12 MulByNonResidue()
        {
            throw new NotSupportedException();
        }
    }
}