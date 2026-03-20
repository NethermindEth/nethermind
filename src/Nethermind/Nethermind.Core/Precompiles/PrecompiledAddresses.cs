// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Precompiles;

/// <summary>
/// Contains static references to all known Ethereum precompile addresses.
/// </summary>
public static class PrecompiledAddresses
{
    public static readonly AddressAsKey ECRecover = Address.FromNumber(0x01);
    public static readonly AddressAsKey Sha256 = Address.FromNumber(0x02);
    public static readonly AddressAsKey Ripemd160 = Address.FromNumber(0x03);
    public static readonly AddressAsKey Identity = Address.FromNumber(0x04);
    public static readonly AddressAsKey ModExp = Address.FromNumber(0x05);
    public static readonly AddressAsKey BN254Add = Address.FromNumber(0x06);
    public static readonly AddressAsKey BN254Mul = Address.FromNumber(0x07);
    public static readonly AddressAsKey BN254PairingCheck = Address.FromNumber(0x08);
    public static readonly AddressAsKey Blake2F = Address.FromNumber(0x09);
    public static readonly AddressAsKey PointEvaluation = Address.FromNumber(0x0a);
    public static readonly AddressAsKey Bls12381G1Add = Address.FromNumber(0x0b);
    public static readonly AddressAsKey Bls12381G1Msm = Address.FromNumber(0x0c);
    public static readonly AddressAsKey Bls12381G2Add = Address.FromNumber(0x0d);
    public static readonly AddressAsKey Bls12381G2Msm = Address.FromNumber(0x0e);
    public static readonly AddressAsKey Bls12381PairingCheck = Address.FromNumber(0x0f);
    public static readonly AddressAsKey Bls12381FpToG1 = Address.FromNumber(0x10);
    public static readonly AddressAsKey Bls12381Fp2ToG2 = Address.FromNumber(0x11);
    public static readonly AddressAsKey P256Verify = Address.FromNumber(0x0100);
    public static readonly AddressAsKey L1Sload = Address.FromNumber(0x10001);
}
