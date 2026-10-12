// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Precompiles;

// Computed on access, so the guest runs no class constructor for this type.
public static partial class PrecompiledAddresses
{
    public static AddressAsKey ECRecover => Address.FromNumber(0x01);
    public static AddressAsKey Sha256 => Address.FromNumber(0x02);
    public static AddressAsKey Ripemd160 => Address.FromNumber(0x03);
    public static AddressAsKey Identity => Address.FromNumber(0x04);
    public static AddressAsKey ModExp => Address.FromNumber(0x05);
    public static AddressAsKey BN254Add => Address.FromNumber(0x06);
    public static AddressAsKey BN254Mul => Address.FromNumber(0x07);
    public static AddressAsKey BN254PairingCheck => Address.FromNumber(0x08);
    public static AddressAsKey Blake2F => Address.FromNumber(0x09);
    public static AddressAsKey PointEvaluation => Address.FromNumber(0x0a);
    public static AddressAsKey Bls12381G1Add => Address.FromNumber(0x0b);
    public static AddressAsKey Bls12381G1Msm => Address.FromNumber(0x0c);
    public static AddressAsKey Bls12381G2Add => Address.FromNumber(0x0d);
    public static AddressAsKey Bls12381G2Msm => Address.FromNumber(0x0e);
    public static AddressAsKey Bls12381PairingCheck => Address.FromNumber(0x0f);
    public static AddressAsKey Bls12381FpToG1 => Address.FromNumber(0x10);
    public static AddressAsKey Bls12381Fp2ToG2 => Address.FromNumber(0x11);
    public static AddressAsKey P256Verify => Address.FromNumber(0x0100);
}
