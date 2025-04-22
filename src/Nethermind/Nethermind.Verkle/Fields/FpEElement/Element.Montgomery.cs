using System.Runtime.CompilerServices;
using FE = Nethermind.Verkle.Fields.FpEElement.FpE;

namespace Nethermind.Verkle.Fields.FpEElement;

public readonly partial struct FpE
{
    /// <summary>
    ///     Compute the Legendre symbol z|p using Euler's criterion. p is a prime, z is relatively prime to p (if p divides z,
    ///     then z|p = 0).
    /// </summary>
    /// <param name="z"></param>
    /// <returns>1 if a has a square root modulo p, -1 otherwise</returns>
    public static int Legendre(in FE z)
    {
        Exp(z, _bLegendreExponentElement.Value, out FE res);

        if (res.IsZero) return 0;

        if (res.IsOne) return 1;

        return -1;
    }

    public bool LexicographicallyLargest()
    {
        FromMontgomery(in this, out FE mont);
        return !SubtractUnderflow(mont, qMinOne, out _);
    }

    public static void InverseOld(in FE x, out FE z)
    {
        if (x.IsZero)
        {
            z = Zero;
            return;
        }

        // initialize u = q
        FE u = qElement;
        // initialize s = r^2
        FE s = rSquare;
        FE r = new(0);
        FE v = x;


        while (true)
        {
            while ((v.u0 & 1) == 0)
            {
                v.RightShiftByOne(out v);
                if ((s.u0 & 1) == 1) AddOverflow(s, qElement, out s);

                s.RightShiftByOne(out s);
            }

            while ((u.u0 & 1) == 0)
            {
                u.RightShiftByOne(out u);
                if ((r.u0 & 1) == 1) AddOverflow(r, qElement, out r);

                r.RightShiftByOne(out r);
            }

            if (!LessThan(v, u))
            {
                SubtractUnderflow(v, u, out v);
                SubtractMod(s, r, out s);
            }
            else
            {
                SubtractUnderflow(u, v, out u);
                SubtractMod(r, s, out r);
            }


            if (u.u0 == 1 && (u.u3 | u.u2 | u.u1) == 0)
            {
                z = r;
                return;
            }

            if (v.u0 == 1 && (v.u3 | v.u2 | v.u1) == 0)
            {
                z = s;
                return;
            }
        }
    }

    public static void MultiplyMod(in FE x, in FE y, out FE res)
    {
        ref ulong rx = ref Unsafe.As<FE, ulong>(ref Unsafe.AsRef(in x));
        ref ulong ry = ref Unsafe.As<FE, ulong>(ref Unsafe.AsRef(in y));

        U4 t = new();
        U3 c = new();
        U4 z = new();

        // round 0
        c.u1 = Math.BigMul(rx, ry, out c.u0);
        ulong m = c.u0 * QInvNeg;
        c.u2 = MAdd0(m, Q0, c.u0);
        c.u1 = MAdd1(rx, Unsafe.Add(ref ry, 1), c.u1, out c.u0);
        c.u2 = MAdd2(m, Q1, c.u2, c.u0, out t.u0);
        c.u1 = MAdd1(rx, Unsafe.Add(ref ry, 2), c.u1, out c.u0);
        c.u2 = MAdd2(m, Q2, c.u2, c.u0, out t.u1);
        c.u1 = MAdd1(rx, Unsafe.Add(ref ry, 3), c.u1, out c.u0);
        t.u3 = MAdd3(m, Q3, c.u0, c.u2, c.u1, out t.u2);

        // round 1
        c.u1 = MAdd1(Unsafe.Add(ref rx, 1), ry, t.u0, out c.u0);
        m = c.u0 * QInvNeg;
        c.u2 = MAdd0(m, Q0, c.u0);
        c.u1 = MAdd2(Unsafe.Add(ref rx, 1), Unsafe.Add(ref ry, 1), c.u1, t.u1, out c.u0);
        c.u2 = MAdd2(m, Q1, c.u2, c.u0, out t.u0);
        c.u1 = MAdd2(Unsafe.Add(ref rx, 1), Unsafe.Add(ref ry, 2), c.u1, t.u2, out c.u0);
        c.u2 = MAdd2(m, Q2, c.u2, c.u0, out t.u1);
        c.u1 = MAdd2(Unsafe.Add(ref rx, 1), Unsafe.Add(ref ry, 3), c.u1, t.u3, out c.u0);
        t.u3 = MAdd3(m, Q3, c.u0, c.u2, c.u1, out t.u2);

        // round 2
        c.u1 = MAdd1(Unsafe.Add(ref rx, 2), ry, t.u0, out c.u0);
        m = c.u0 * QInvNeg;
        c.u2 = MAdd0(m, Q0, c.u0);
        c.u1 = MAdd2(Unsafe.Add(ref rx, 2), Unsafe.Add(ref ry, 1), c.u1, t.u1, out c.u0);
        c.u2 = MAdd2(m, Q1, c.u2, c.u0, out t.u0);
        c.u1 = MAdd2(Unsafe.Add(ref rx, 2), Unsafe.Add(ref ry, 2), c.u1, t.u2, out c.u0);
        c.u2 = MAdd2(m, Q2, c.u2, c.u0, out t.u1);
        c.u1 = MAdd2(Unsafe.Add(ref rx, 2), Unsafe.Add(ref ry, 3), c.u1, t.u3, out c.u0);
        t.u3 = MAdd3(m, Q3, c.u0, c.u2, c.u1, out t.u2);

        // round 3
        c.u1 = MAdd1(Unsafe.Add(ref rx, 3), ry, t.u0, out c.u0);
        m = c.u0 * QInvNeg;
        c.u2 = MAdd0(m, Q0, c.u0);
        c.u1 = MAdd2(Unsafe.Add(ref rx, 3), Unsafe.Add(ref ry, 1), c.u1, t.u1, out c.u0);
        c.u2 = MAdd2(m, Q1, c.u2, c.u0, out z.u0);
        c.u1 = MAdd2(Unsafe.Add(ref rx, 3), Unsafe.Add(ref ry, 2), c.u1, t.u2, out c.u0);
        c.u2 = MAdd2(m, Q2, c.u2, c.u0, out z.u1);
        c.u1 = MAdd2(Unsafe.Add(ref rx, 3), Unsafe.Add(ref ry, 3), c.u1, t.u3, out c.u0);
        z.u3 = MAdd3(m, Q3, c.u0, c.u2, c.u1, out z.u2);


        Unsafe.SkipInit(out res);
        Unsafe.As<FE, U4>(ref res) = z;
        if (LessThan(qElement, res)) SubtractUnderflow(res, qElement, out res);
    }

    public static FE[] MultiInverse(in ReadOnlySpan<FE> values)
    {
        if (values.Length == 0) return Array.Empty<FE>();

        FE[] results = new FE[values.Length];
        bool[] zeros = new bool[values.Length];

        FE accumulator = One;

        for (int i = 0; i < values.Length; i++)
        {
            if (values[i].IsZero)
            {
                zeros[i] = true;
                continue;
            }

            results[i] = accumulator;
            MultiplyMod(in accumulator, in values[i], out accumulator);
        }

        Inverse(in accumulator, out accumulator);

        for (int i = values.Length - 1; i >= 0; i--)
        {
            if (zeros[i]) continue;
            MultiplyMod(in results[i], in accumulator, out results[i]);
            MultiplyMod(in accumulator, in values[i], out accumulator);
        }

        return results;
    }

    /// <summary>
    ///     Sqrt z = √x (mod q)
    ///     if the square root doesn't exist (x is not a square mod q)
    ///     Sqrt returns false
    /// </summary>
    /// <param name="x"></param>
    /// <param name="z"></param>
    /// <returns>if the square root doesn't exist Sqrt returns false, else true</returns>
    public static bool SqrtOld(in FE x, out FE z)
    {
        // q ≡ 1 (mod 4)
        // see modSqrtTonelliShanks in math/big/int.go
        // using https://www.maa.org/sites/default/files/pdf/upload_library/22/Polya/07468342.di020786.02p0470a.pdf

        // w = x^CONST, where CONST=((s-1)/2))
        Exp(in x, _bSqrtExponentElement.Value, out FE w);

        // y = x^((s+1)/2)) = w * x
        MultiplyMod(x, w, out FE y);

        // b = x^s = w * w * x = y * x
        MultiplyMod(w, y, out FE b);

        ulong r = SqrtR;

        // compute legendre symbol
        // t = x^((q-1)/2) = r-1 squaring of xˢ
        FE t = b;

        for (ulong i = 0; i < r - 1; i++) MultiplyMod(in t, in t, out t);

        if (t.IsZero)
        {
            z = Zero;
            return true;
        }

        if (!t.IsOne)
        {
            // t != 1, we don't have a square root
            z = Zero;
            return false;
        }

        // g = nonResidue ^ s
        FE g = gResidue;
        while (true)
        {
            ulong m = 0;
            t = b;

            while (!t.IsOne)
            {
                MultiplyMod(in t, in t, out t);
                m++;
            }

            if (m == 0)
            {
                z = y;
                return true;
            }

            // t = g^(2^(r-m-1)) (mod q)
            int ge = (int)(r - m - 1);
            t = g;
            while (ge > 0)
            {
                MultiplyMod(in t, in t, out t);
                ge--;
            }

            MultiplyMod(in t, in t, out g);
            MultiplyMod(in y, in t, out y);
            MultiplyMod(in b, in g, out b);
            r = m;
        }
    }
}
