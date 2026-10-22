// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Precompiles;

/// <summary>
/// Contains static references to all known Ethereum precompile addresses.
/// </summary>
public static class PrecompiledAddresses
{
    public static readonly Address ECRecover = Precompile(0x01);
    public static readonly Address Sha256 = Precompile(0x02);
    public static readonly Address Ripemd160 = Precompile(0x03);
    public static readonly Address Identity = Precompile(0x04);
    public static readonly Address ModExp = Precompile(0x05);
    public static readonly Address BN254Add = Precompile(0x06);
    public static readonly Address BN254Mul = Precompile(0x07);
    public static readonly Address BN254PairingCheck = Precompile(0x08);
    public static readonly Address Blake2F = Precompile(0x09);
    public static readonly Address PointEvaluation = Precompile(0x0a);
    public static readonly Address Bls12381G1Add = Precompile(0x0b);
    public static readonly Address Bls12381G1Msm = Precompile(0x0c);
    public static readonly Address Bls12381G2Add = Precompile(0x0d);
    public static readonly Address Bls12381G2Msm = Precompile(0x0e);
    public static readonly Address Bls12381PairingCheck = Precompile(0x0f);
    public static readonly Address Bls12381FpToG1 = Precompile(0x10);
    public static readonly Address Bls12381Fp2ToG2 = Precompile(0x11);
    public static readonly Address P256Verify = Precompile(0x100);

    /// <remarks>Avoids <see cref="Address.FromNumber"/>, whose shifts stop ILC from precomputing this type for the zkEVM guest.</remarks>
    private static Address Precompile(int number)
    {
        byte[] bytes = new byte[Address.Size];
        bytes[^2] = (byte)(number / 256);
        bytes[^1] = (byte)number;
        return new Address(bytes);
    }
}
