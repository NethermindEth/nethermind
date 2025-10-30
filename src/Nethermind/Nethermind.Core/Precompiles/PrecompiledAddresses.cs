// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;

namespace Nethermind.Core.Precompiles;

/// <summary>
/// Contains static references to all known Ethereum precompile addresses.
/// </summary>
public static class PrecompiledAddresses
{
    public static readonly Box<Address> EcRecover = Address.FromNumber(0x01);
    public static readonly Box<Address> Sha256 = Address.FromNumber(0x02);
    public static readonly Box<Address> Ripemd160 = Address.FromNumber(0x03);
    public static readonly Box<Address> Identity = Address.FromNumber(0x04);
    public static readonly Box<Address> ModExp = Address.FromNumber(0x05);
    public static readonly Box<Address> Bn128Add = Address.FromNumber(0x06);
    public static readonly Box<Address> Bn128Mul = Address.FromNumber(0x07);
    public static readonly Box<Address> Bn128Pairing = Address.FromNumber(0x08);
    public static readonly Box<Address> Blake2F = Address.FromNumber(0x09);
    public static readonly Box<Address> PointEvaluation = Address.FromNumber(0x0a);
    public static readonly Box<Address> Bls12G1Add = Address.FromNumber(0x0b);
    public static readonly Box<Address> Bls12G1Mul = Address.FromNumber(0x0c);
    public static readonly Box<Address> Bls12G1MultiExp = Address.FromNumber(0x0d);
    public static readonly Box<Address> Bls12G2Add = Address.FromNumber(0x0e);
    public static readonly Box<Address> Bls12G2Mul = Address.FromNumber(0x0f);
    public static readonly Box<Address> Bls12G2MultiExp = Address.FromNumber(0x10);
    public static readonly Box<Address> Bls12Pairing = Address.FromNumber(0x11);
    public static readonly Box<Address> P256Verify = Address.FromNumber(0x0100);
    public static readonly Box<Address> L1Sload = Address.FromNumber(0x10001);
}
