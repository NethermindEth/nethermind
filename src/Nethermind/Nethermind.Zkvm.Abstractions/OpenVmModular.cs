// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.Zkvm.Abstractions;

/// <summary>
/// Arithmetic modulo the 256-bit moduli of OpenVM's standard VM configuration, on its modular extension.
/// </summary>
/// <remarks>
/// Each modulus has its own instructions, and each must run its setup instruction once before the first of them.
/// The extension does not constrain a result to be reduced: <see cref="AssertReduced"/> proves it, as
/// openvm-algebra's <c>assert_reduced</c> does, by comparing the value with itself.
/// </remarks>
public static unsafe partial class OpenVmModular
{
    private static uint s_setUp;

    /// <summary>Returns the index of the modulus the four little-endian limbs spell, or -1 if the VM has none.</summary>
    public static int IndexOf(ulong u0, ulong u1, ulong u2, ulong u3) => u3 switch
    {
        0x30644e72e131a029UL => u2 == 0xb85045b68181585dUL && u1 == 0x2833e84879b97091UL && u0 == 0x43e1f593f0000001UL ? 0 : u2 == 0xb85045b68181585dUL && u1 == 0x97816a916871ca8dUL && u0 == 0x3c208c16d87cfd47UL ? 1 : -1,
        0xffffffffffffffffUL => u2 == 0xffffffffffffffffUL && u1 == 0xffffffffffffffffUL && u0 == 0xfffffffefffffc2fUL ? 2 : u2 == 0xfffffffffffffffeUL && u1 == 0xbaaedce6af48a03bUL && u0 == 0xbfd25e8cd0364141UL ? 3 : -1,
        0xffffffff00000001UL when u2 == 0x0000000000000000UL && u1 == 0x00000000ffffffffUL && u0 == 0xffffffffffffffffUL => 4,
        0xffffffff00000000UL when u2 == 0xffffffffffffffffUL && u1 == 0xbce6faada7179e84UL && u0 == 0xf3b9cac2fc632551UL => 5,
        0x73eda753299d7d48UL when u2 == 0x3339d80809a1d805UL && u1 == 0x53bda402fffe5bfeUL && u0 == 0xffffffff00000001UL => 6,
        _ => -1,
    };

    /// <summary>Writes <paramref name="a"/> + <paramref name="b"/> modulo the modulus at <paramref name="index"/> to <paramref name="result"/>.</summary>
    public static void Add(int index, void* result, void* a, void* b)
    {
        EnsureSetUp(index);
        switch (index)
        {
            case 0: AddBn254Scalar((nuint)result, (nuint)a, (nuint)b); break;
            case 1: AddBn254Base((nuint)result, (nuint)a, (nuint)b); break;
            case 2: AddSecp256k1Base((nuint)result, (nuint)a, (nuint)b); break;
            case 3: AddSecp256k1Scalar((nuint)result, (nuint)a, (nuint)b); break;
            case 4: AddP256Base((nuint)result, (nuint)a, (nuint)b); break;
            case 5: AddP256Scalar((nuint)result, (nuint)a, (nuint)b); break;
            case 6: AddBls12381Scalar((nuint)result, (nuint)a, (nuint)b); break;
        }
    }

    /// <summary>Writes <paramref name="a"/> × <paramref name="b"/> modulo the modulus at <paramref name="index"/> to <paramref name="result"/>.</summary>
    public static void Multiply(int index, void* result, void* a, void* b)
    {
        EnsureSetUp(index);
        switch (index)
        {
            case 0: MulBn254Scalar((nuint)result, (nuint)a, (nuint)b); break;
            case 1: MulBn254Base((nuint)result, (nuint)a, (nuint)b); break;
            case 2: MulSecp256k1Base((nuint)result, (nuint)a, (nuint)b); break;
            case 3: MulSecp256k1Scalar((nuint)result, (nuint)a, (nuint)b); break;
            case 4: MulP256Base((nuint)result, (nuint)a, (nuint)b); break;
            case 5: MulP256Scalar((nuint)result, (nuint)a, (nuint)b); break;
            case 6: MulBls12381Scalar((nuint)result, (nuint)a, (nuint)b); break;
        }
    }

    /// <summary>Proves <paramref name="value"/> is less than the modulus at <paramref name="index"/>; a proof of an unreduced value fails.</summary>
    public static void AssertReduced(int index, void* value)
    {
        EnsureSetUp(index);
        switch (index)
        {
            case 0: IsEqBn254Scalar((nuint)value, (nuint)value); break;
            case 1: IsEqBn254Base((nuint)value, (nuint)value); break;
            case 2: IsEqSecp256k1Base((nuint)value, (nuint)value); break;
            case 3: IsEqSecp256k1Scalar((nuint)value, (nuint)value); break;
            case 4: IsEqP256Base((nuint)value, (nuint)value); break;
            case 5: IsEqP256Scalar((nuint)value, (nuint)value); break;
            case 6: IsEqBls12381Scalar((nuint)value, (nuint)value); break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EnsureSetUp(int index)
    {
        uint bit = 1u << index;
        if ((s_setUp & bit) == 0)
            SetUp(index, bit);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SetUp(int index, uint bit)
    {
        switch (index)
        {
            case 0: ModuliSetupBn254Scalar(); break;
            case 1: ModuliSetupBn254Base(); break;
            case 2: ModuliSetupSecp256k1Base(); break;
            case 3: ModuliSetupSecp256k1Scalar(); break;
            case 4: ModuliSetupP256Base(); break;
            case 5: ModuliSetupP256Scalar(); break;
            case 6: ModuliSetupBls12381Scalar(); break;
        }

        s_setUp |= bit;
    }
}
