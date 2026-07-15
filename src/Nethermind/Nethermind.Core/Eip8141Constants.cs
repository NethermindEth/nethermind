// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
    public const ulong Secp256k1VerificationGasCost = 2_800;
    public const ulong P256VerificationGasCost = 6_700;
    public const int ExpiryDataLength = 8;

    public static readonly Address EntryPointAddress = new("0x00000000000000000000000000000000000000aa");
    public static readonly Address ExpiryVerifierAddress = new("0x0000000000000000000000000000000000008141");
}
