// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Core;

/// <summary>
/// Constants of EIP-8141 frame transactions.
/// https://eips.ethereum.org/EIPS/eip-8141
/// </summary>
public static class Eip8141Constants
{
    public const int MaxFrames = 64;
    public const long IntrinsicGasCost = 15_000;
    public const long PerFrameGasCost = 475;
    public const ulong ArbitraryVerificationGasCost = 100;
    public const ulong Secp256k1VerificationGasCost = 2_800;
    public const ulong P256VerificationGasCost = 6_700;
    public const int ExpiryDataLength = 8;

    // EIP-8141: consensus cap on validation-prefix gas, used by the prefix execution/simulation path.
    public const ulong MaxVerifyGas = 100_000;

    public static readonly Address EntryPointAddress = new("0x00000000000000000000000000000000000000aa");
    public static readonly Address ExpiryVerifierAddress = new("0x0000000000000000000000000000000000008141");

    /// <summary>
    /// The expiry-verifier predeploy runtime code that clients must install at
    /// <see cref="ExpiryVerifierAddress"/> when EIP-8141 activates.
    /// </summary>
    public static readonly byte[] ExpiryVerifierCode = Bytes.FromHexString("0x60083614600a575f5ffd5b5f3560c01c4211601657005b5f5ffd");

    /// <summary>
    /// Keccak of <see cref="ExpiryVerifierCode"/>. The <c>TIMESTAMP</c> validation-prefix exemption
    /// applies only to a frame whose <see cref="ExpiryVerifierAddress"/> code hash matches this value.
    /// </summary>
    public static readonly ValueHash256 ExpiryVerifierCodeHash = ValueKeccak.Compute(ExpiryVerifierCode);
}
