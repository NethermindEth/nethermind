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

using System.Collections.Generic;
using System.Numerics;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Core.Crypto.ZkSnarks
{
    /// <summary>
    ///     Code adapted from ethereumJ (https://github.com/ethereum/ethereumj)
    /// </summary>
    public class PairingCheck
    {
        public static readonly BigInteger LoopCount = BigInteger.Parse("29793968203157093288");

        private readonly List<Pair> _pairs = new List<Pair>();
        private Fp12 _product = Fp12.One;

        private PairingCheck()
        {
        }

        public static PairingCheck Create()
        {
            return new PairingCheck();
        }

        public void AddPair(Bn128Fp g1, Bn128Fp2 g2)
        {
            _pairs.Add(new Pair(g1, g2));
        }

        public void Run()
        {
            for (int i = 0; i < _pairs.Count; i++)
            {
                Fp12 miller = _pairs[i].MillerLoop();

                if (!miller.Equals(Fp12.One)) // run mul code only if necessary
                {
                    _product = _product.Mul(miller);
                }
            }

            // finalize
            _product = FinalExponentiation(_product);
        }

        public UInt256 Result()
        {
            return _product.Equals(Fp12.One) ? UInt256.One : UInt256.Zero;
        }

        private static Fp12 MillerLoop(Bn128Fp g1, Bn128Fp2 g2)
        {
            // convert to affine coordinates
            g1 = g1.ToAffine();
            g2 = g2.ToAffine();

            // calculate Ell coefficients
            List<EllCoeffs> coeffs = CalcEllCoeffs(g2);

            Fp12 f = Fp12.One;
            int idx = 0;

            // for each bit except most significant one
            for (int i = LoopCount.BitLength() - 2; i >= 0; i--)
            {
                EllCoeffs cInLoop = coeffs[idx++];
                f = f.Squared();
                f = f.MulBy024(cInLoop.Ell0, g1.Y.Mul(cInLoop.EllVw), g1.X.Mul(cInLoop.EllVv));

                if (LoopCount.TestBit(i))
                {
                    cInLoop = coeffs[idx++];
                    f = f.MulBy024(cInLoop.Ell0, g1.Y.Mul(cInLoop.EllVw), g1.X.Mul(cInLoop.EllVv));
                }
            }

            EllCoeffs c = coeffs[idx++];
            f = f.MulBy024(c.Ell0, g1.Y.Mul(c.EllVw), g1.X.Mul(c.EllVv));

            c = coeffs[idx];
            f = f.MulBy024(c.Ell0, g1.Y.Mul(c.EllVw), g1.X.Mul(c.EllVv));

            return f;
        }

        private static List<EllCoeffs> CalcEllCoeffs(Bn128Fp2 baseElement)
        {
            List<EllCoeffs> coeffs = new List<EllCoeffs>();

            Bn128Fp2 addend = baseElement;

            // for each bit except most significant one
            for (int i = LoopCount.BitLength() - 2; i >= 0; i--)
            {
                Precomputed doubling = FlippedMillerLoopDoubling(addend);

                addend = doubling.G2;
                coeffs.Add(doubling.Coeffs);

                if (LoopCount.TestBit(i))
                {
                    Precomputed additionInLoop = FlippedMillerLoopMixedAddition(baseElement, addend);
                    addend = additionInLoop.G2;
                    coeffs.Add(additionInLoop.Coeffs);
                }
            }

            Bn128Fp2 q1 = baseElement.MulByP();
            Bn128Fp2 q2 = q1.MulByP();

            q2 = new Bn128Fp2(q2.X, q2.Y.Negate(), q2.Z); // q2.y = -q2.y

            Precomputed addition = FlippedMillerLoopMixedAddition(q1, addend);
            addend = addition.G2;
            coeffs.Add(addition.Coeffs);

            addition = FlippedMillerLoopMixedAddition(q2, addend);
            coeffs.Add(addition.Coeffs);

            return coeffs;
        }

        private static Precomputed FlippedMillerLoopMixedAddition(Bn128Fp2 baseElement, Bn128Fp2 addend)
        {
            Fp2 x1 = addend.X, y1 = addend.Y, z1 = addend.Z;
            Fp2 x2 = baseElement.X, y2 = baseElement.Y;

            Fp2 d = x1.Sub(x2.Mul(z1)); // d = x1 - x2 * z1
            Fp2 e = y1.Sub(y2.Mul(z1)); // e = y1 - y2 * z1
            Fp2 f = d.Squared(); // f = d^2
            Fp2 g = e.Squared(); // g = e^2
            Fp2 h = d.Mul(f); // h = d * f
            Fp2 i = x1.Mul(f); // i = x1 * f
            Fp2 j = h.Add(z1.Mul(g)).Sub(i.Double()); // j = h + z1 * g - 2 * i

            Fp2 x3 = d.Mul(j); // x3 = d * j
            Fp2 y3 = e.Mul(i.Sub(j)).Sub(h.Mul(y1)); // y3 = e * (i - j) - h * y1)
            Fp2 z3 = z1.Mul(h); // z3 = Z1*H

            Fp2 ell0 = Parameters.Twist.Mul(e.Mul(x2).Sub(d.Mul(y2))); // ell_0 = TWIST * (e * x2 - d * y2)
            Fp2 ellVv = e.Negate(); // ell_VV = -e
            Fp2 ellVw = d; // ell_VW = d

            return new Precomputed(
                new Bn128Fp2(x3, y3, z3),
                new EllCoeffs(ell0, ellVw, ellVv)
            );
        }

        private static Precomputed FlippedMillerLoopDoubling(Bn128Fp2 g2)
        {
            Fp2 x = g2.X, y = g2.Y, z = g2.Z;

            Fp2 a = Fp.InverseOf2.Mul(x.Mul(y)); // a = x * y / 2
            Fp2 b = y.Squared(); // b = y^2
            Fp2 c = z.Squared(); // c = z^2
            Fp2 d = c.Add(c).Add(c); // d = 3 * c
            Fp2 e = Parameters.Fp2B.Mul(d); // e = twist_b * d
            Fp2 f = e.Add(e).Add(e); // f = 3 * e
            Fp2 g = Fp.InverseOf2.Mul(b.Add(f)); // g = (b + f) / 2
            Fp2 h = y.Add(z).Squared().Sub(b.Add(c)); // h = (y + z)^2 - (b + c)
            Fp2 i = e.Sub(b); // i = e - b
            Fp2 j = x.Squared(); // j = x^2
            Fp2 e2 = e.Squared(); // e2 = e^2

            Fp2 rx = a.Mul(b.Sub(f)); // rx = a * (b - f)
            Fp2 ry = g.Squared().Sub(e2.Add(e2).Add(e2)); // ry = g^2 - 3 * e^2
            Fp2 rz = b.Mul(h); // rz = b * h

            Fp2 ell0 = Parameters.Twist.Mul(i); // ell_0 = twist * i
            Fp2 ellVw = h.Negate(); // ell_VW = -h
            Fp2 ellVv = j.Add(j).Add(j); // ell_VV = 3 * j

            return new Precomputed(
                new Bn128Fp2(rx, ry, rz),
                new EllCoeffs(ell0, ellVw, ellVv)
            );
        }

        public static Fp12 FinalExponentiation(Fp12 el)
        {
            // first chunk
            Fp12 w = new Fp12(el.A, el.B.Negate()); // el.b = -el.b
            Fp12 x = el.Inverse();
            Fp12 y = w.Mul(x);
            Fp12 z = y.FrobeniusMap(2);
            Fp12 pre = z.Mul(y);

            // last chunk
            Fp12 a = pre.NegExp(Parameters.PairingFinalExponentZ);
            Fp12 b = a.CyclotomicSquare();
            Fp12 c = b.CyclotomicSquare();
            Fp12 d = c.Mul(b);
            Fp12 e = d.NegExp(Parameters.PairingFinalExponentZ);
            Fp12 f = e.CyclotomicSquare();
            Fp12 g = f.NegExp(Parameters.PairingFinalExponentZ);
            Fp12 h = d.UnitaryInverse();
            Fp12 i = g.UnitaryInverse();
            Fp12 j = i.Mul(e);
            Fp12 k = j.Mul(h);
            Fp12 l = k.Mul(b);
            Fp12 m = k.Mul(e);
            Fp12 n = m.Mul(pre);
            Fp12 o = l.FrobeniusMap(1);
            Fp12 p = o.Mul(n);
            Fp12 q = k.FrobeniusMap(2);
            Fp12 r = q.Mul(p);
            Fp12 s = pre.UnitaryInverse();
            Fp12 t = s.Mul(l);
            Fp12 u = t.FrobeniusMap(3);
            Fp12 v = u.Mul(r);

            return v;
        }

        private class Precomputed
        {
            public Bn128Fp2 G2 { get; }
            public EllCoeffs Coeffs { get; }

            public Precomputed(Bn128Fp2 g2, EllCoeffs coeffs)
            {
                G2 = g2;
                Coeffs = coeffs;
            }
        }

        public class Pair
        {
            private Bn128Fp G1 { get; }
            private Bn128Fp2 G2 { get; }

            public Pair(Bn128Fp g1, Bn128Fp2 g2)
            {
                G1 = g1;
                G2 = g2;
            }

            public Fp12 MillerLoop()
            {
                // miller loop result equals "1" if at least one of the points is zero
                if (G1.IsZero() || G2.IsZero())
                {
                    return Fp12.One;
                }

                return PairingCheck.MillerLoop(G1, G2);
            }
        }

        private class EllCoeffs
        {
            public Fp2 Ell0 { get; }
            public Fp2 EllVw { get; }
            public Fp2 EllVv { get; }

            public EllCoeffs(Fp2 ell0, Fp2 ellVw, Fp2 ellVv)
            {
                Ell0 = ell0;
                EllVw = ellVw;
                EllVv = ellVv;
            }
        }
    }
}