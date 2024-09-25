using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Nethermind.Int256;
using FE = Nethermind.Verkle.Fields.FpEElement.FpE;

namespace Nethermind.Verkle.Fields.FpEElement;

public readonly partial struct FpE
{
    public FE Negative()
    {
        SubtractMod(Zero, this, out FE res);
        return res;
    }

    public void LeftShift(int n, out FE res)
    {
        Lsh(this, n, out res);
    }

    public void RightShift(int n, out FE res)
    {
        Rsh(this, n, out res);
    }

    private void RightShiftByOne(out FE res)
    {
        res = new FE(
            (u0 >> 1) | (u1 << 63),
            (u1 >> 1) | (u2 << 63),
            (u2 >> 1) | (u3 << 63),
            u3 >> 1
        );
    }

    public static void AddMod(in FE a, in FE b, out FE res)
    {
        bool overflow = AddOverflow(a, b, out res);
        if (overflow)
        {
            SubtractUnderflow(res, qElement, out res);
            return;
        }

        if (!LessThanSubModulus(res))
            if (SubtractUnderflow(res, qElement, out res))
                ThrowInvalidConstraintException();
    }

    public static void Divide(in FE x, in FE y, out FE z)
    {
        Inverse(y, out FE yInv);
        MultiplyMod(x, yInv, out z);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SubtractMod(in FE a, in FE b, out FE res)
    {
        if (SubtractUnderflow(a, b, out res))
            AddOverflow(qElement, res, out res);
    }

    public static void Exp(in FE b, in UInt256 e, out FE result)
    {
        result = One;
        FE bs = b;
        int len = e.BitLen;
        for (int i = 0; i < len; i++)
        {
            if (e.Bit(i)) MultiplyMod(result, bs, out result);

            MultiplyMod(bs, bs, out bs);
        }
    }

    public static void Lsh(in FE x, int n, out FE res)
    {
        if (n % 64 == 0)
        {
            switch (n)
            {
                case 0:
                    res = x;
                    return;
                case 64:
                    x.Lsh64(out res);
                    return;
                case 128:
                    x.Lsh128(out res);
                    return;
                case 192:
                    x.Lsh192(out res);
                    return;
                default:
                    res = Zero;
                    return;
            }
        }

        res = Zero;
        ulong z0 = res.u0, z1 = res.u1, z2 = res.u2, z3 = res.u3;
        ulong a = 0, b = 0;
        // Big swaps first
        if (n > 192)
        {
            if (n > 256)
            {
                res = Zero;
                return;
            }

            x.Lsh192(out res);
            n -= 192;
            goto sh192;
        }

        if (n > 128)
        {
            x.Lsh128(out res);
            n -= 128;
            goto sh128;
        }

        if (n > 64)
        {
            x.Lsh64(out res);
            n -= 64;
            goto sh64;
        }

        res = x;

        // remaining shifts
        a = Rsh(res.u0, 64 - n);
        z0 = Lsh(res.u0, n);

        sh64:
        b = Rsh(res.u1, 64 - n);
        z1 = Lsh(res.u1, n) | a;

        sh128:
        a = Rsh(res.u2, 64 - n);
        z2 = Lsh(res.u2, n) | b;

        sh192:
        z3 = Lsh(res.u3, n) | a;

        res = new FE(z0, z1, z2, z3);
    }


    public static void Rsh(in FE x, int n, out FE res)
    {
        // n % 64 == 0
        if ((n & 0x3f) == 0)
        {
            switch (n)
            {
                case 0:
                    res = x;
                    return;
                case 64:
                    x.Rsh64(out res);
                    return;
                case 128:
                    x.Rsh128(out res);
                    return;
                case 192:
                    x.Rsh192(out res);
                    return;
                default:
                    res = Zero;
                    return;
            }
        }

        res = Zero;
        ulong z0 = res.u0, z1 = res.u1, z2 = res.u2, z3 = res.u3;
        ulong a = 0, b = 0;
        // Big swaps first
        if (n > 192)
        {
            if (n > 256)
            {
                res = Zero;
                return;
            }

            x.Rsh192(out res);
            z0 = res.u0;
            z1 = res.u1;
            z2 = res.u2;
            z3 = res.u3;
            n -= 192;
            goto sh192;
        }

        if (n > 128)
        {
            x.Rsh128(out res);
            z0 = res.u0;
            z1 = res.u1;
            z2 = res.u2;
            z3 = res.u3;
            n -= 128;
            goto sh128;
        }

        if (n > 64)
        {
            x.Rsh64(out res);
            z0 = res.u0;
            z1 = res.u1;
            z2 = res.u2;
            z3 = res.u3;
            n -= 64;
            goto sh64;
        }

        res = x;
        z0 = res.u0;
        z1 = res.u1;
        z2 = res.u2;
        z3 = res.u3;

        // remaining shifts
        a = Lsh(res.u3, 64 - n);
        z3 = Rsh(res.u3, n);

        sh64:
        b = Lsh(res.u2, 64 - n);
        z2 = Rsh(res.u2, n) | a;

        sh128:
        a = Lsh(res.u1, 64 - n);
        z1 = Rsh(res.u1, n) | b;

        sh192:
        z0 = Rsh(res.u0, n) | a;

        res = new FE(z0, z1, z2, z3);
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Lsh64(out FE res)
    {
        res = new FE(0, u0, u1, u2);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Lsh128(out FE res)
    {
        res = new FE(0, 0, u0, u1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Lsh192(out FE res)
    {
        res = new FE(0, 0, 0, u0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Rsh64(out FE res)
    {
        res = new FE(u1, u2, u3);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Rsh128(out FE res)
    {
        res = new FE(u2, u3);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Rsh192(out FE res)
    {
        res = new FE(u3);
    }

    public static bool SubtractUnderflow(in FE a, in FE b, out FE res)
    {
        if (Avx2.IsSupported)
        {
            Vector256<ulong> av = Unsafe.As<FE, Vector256<ulong>>(ref Unsafe.AsRef(in a));
            Vector256<ulong> bv = Unsafe.As<FE, Vector256<ulong>>(ref Unsafe.AsRef(in b));

            Vector256<ulong> result = Avx2.Subtract(av, bv);
            // Invert top bits as Avx2.CompareGreaterThan is only available for longs, not unsigned
            Vector256<ulong> resultSigned = Avx2.Xor(result, Vector256.Create<ulong>(0x8000_0000_0000_0000));
            Vector256<ulong> avSigned = Avx2.Xor(av, Vector256.Create<ulong>(0x8000_0000_0000_0000));

            // Which vectors need to borrow from the next
            Vector256<long> vBorrow = Avx2.CompareGreaterThan(
                Unsafe.As<Vector256<ulong>, Vector256<long>>(ref resultSigned),
                Unsafe.As<Vector256<ulong>, Vector256<long>>(ref avSigned));

            // Move borrow from Vector space to int
            int borrow = Avx.MoveMask(Unsafe.As<Vector256<long>, Vector256<double>>(ref vBorrow));

            // All zeros will cascade another borrow when borrow is subtracted from it
            Vector256<ulong> vCascade = Avx2.CompareEqual(result, Vector256<ulong>.Zero);
            // Move cascade from Vector space to int
            int cascade = Avx.MoveMask(Unsafe.As<Vector256<ulong>, Vector256<double>>(ref vCascade));

            // Use ints to work out the Vector cross lane cascades
            // Move borrow to next bit and add cascade
            borrow = cascade + (2 * borrow); // lea
            // Remove cascades not effected by borrow
            cascade ^= borrow;
            // Choice of 16 vectors
            cascade &= 0x0f;

            // Lookup the borrows to broadcast to the Vectors
            Vector256<ulong> cascadedBorrows =
                Unsafe.Add(ref Unsafe.As<byte, Vector256<ulong>>(ref MemoryMarshal.GetReference(SBroadcastLookup)),
                    cascade);

            // Mark res as initalized so we can use it as left said of ref assignment
            Unsafe.SkipInit(out res);
            // Subtract the cascadedBorrows from the result
            Unsafe.As<FE, Vector256<ulong>>(ref res) = Avx2.Subtract(result, cascadedBorrows);
            return (borrow & 0b1_0000) != 0;
        }
        else
        {
            ref ulong rx = ref Unsafe.As<FE, ulong>(ref Unsafe.AsRef(in a));
            ref ulong ry = ref Unsafe.As<FE, ulong>(ref Unsafe.AsRef(in b));
            ulong borrow = 0ul;
            SubtractWithBorrow(rx, ry, ref borrow, out ulong res0);
            SubtractWithBorrow(Unsafe.Add(ref rx, 1), Unsafe.Add(ref ry, 1), ref borrow, out ulong res1);
            SubtractWithBorrow(Unsafe.Add(ref rx, 2), Unsafe.Add(ref ry, 2), ref borrow, out ulong res2);
            SubtractWithBorrow(Unsafe.Add(ref rx, 3), Unsafe.Add(ref ry, 3), ref borrow, out ulong res3);
            res = new FE(res0, res1, res2, res3);
            return borrow != 0;
        }
    }

    public static bool AddOverflow(in FE a, in FE b, out FE res)
    {
        if (Avx2.IsSupported)
        {
            Vector256<ulong> av = Unsafe.As<FE, Vector256<ulong>>(ref Unsafe.AsRef(in a));
            Vector256<ulong> bv = Unsafe.As<FE, Vector256<ulong>>(ref Unsafe.AsRef(in b));

            Vector256<ulong> result = Avx2.Add(av, bv);
            Vector256<ulong> carryFromBothHighBits = Avx2.And(av, bv);
            Vector256<ulong> eitherHighBit = Avx2.Or(av, bv);
            Vector256<ulong> highBitNotInResult = Avx2.AndNot(result, eitherHighBit);

            // Set high bits where carry occurs
            Vector256<ulong> vCarry = Avx2.Or(carryFromBothHighBits, highBitNotInResult);
            // Move carry from Vector space to int
            int carry = Avx.MoveMask(Unsafe.As<Vector256<ulong>, Vector256<double>>(ref vCarry));

            // All bits set will cascade another carry when carry is added to it
            Vector256<ulong> vCascade = Avx2.CompareEqual(result, Vector256<ulong>.AllBitsSet);
            // Move cascade from Vector space to int
            int cascade = Avx.MoveMask(Unsafe.As<Vector256<ulong>, Vector256<double>>(ref vCascade));

            // Use ints to work out the Vector cross lane cascades
            // Move carry to next bit and add cascade
            carry = cascade + (2 * carry); // lea
            // Remove cascades not effected by carry
            cascade ^= carry;
            // Choice of 16 vectors
            cascade &= 0x0f;

            // Lookup the carries to broadcast to the Vectors
            Vector256<ulong> cascadedCarries =
                Unsafe.Add(ref Unsafe.As<byte, Vector256<ulong>>(ref MemoryMarshal.GetReference(SBroadcastLookup)),
                    cascade);

            // Mark res as initalized so we can use it as left said of ref assignment
            Unsafe.SkipInit(out res);
            // Add the cascadedCarries to the result
            Unsafe.As<FE, Vector256<ulong>>(ref res) = Avx2.Add(result, cascadedCarries);
            return (carry & 0b1_0000) != 0;
        }
        else
        {
            ref ulong rx = ref Unsafe.As<FE, ulong>(ref Unsafe.AsRef(in a));
            ref ulong ry = ref Unsafe.As<FE, ulong>(ref Unsafe.AsRef(in b));
            ulong carry = 0ul;
            AddWithCarry(rx, ry, ref carry, out ulong res0);
            AddWithCarry(Unsafe.Add(ref rx, 1), Unsafe.Add(ref ry, 1), ref carry, out ulong res1);
            AddWithCarry(Unsafe.Add(ref rx, 2), Unsafe.Add(ref ry, 2), ref carry, out ulong res2);
            AddWithCarry(Unsafe.Add(ref rx, 3), Unsafe.Add(ref ry, 3), ref carry, out ulong res3);
            res = new FE(res0, res1, res2, res3);
            return carry != 0;
        }
    }
}
