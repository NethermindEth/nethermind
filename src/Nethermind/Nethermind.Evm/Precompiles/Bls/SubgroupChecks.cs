// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Int256;

namespace Nethermind.Evm.Precompiles.Bls;

/// <summary>
/// https://eips.ethereum.org/EIPS/eip-2537
/// </summary>
public class SubgroupChecks
{
    public static readonly UInt256 Beta = new((byte[])[0x5F,0x19,0x67,0x2F,0xDF,0x76,0xCE,0x51,0xBA,0x69,0xC6,0x07,0x6A,0x0F,0x77,0xEA,0xDD,0xB3,0xA9,0x3B,0xE6,0xF8,0x96,0x88,0xDE,0x17,0xD8,0x13,0x62,0x0A,0x00,0x02,0x2E,0x01,0xFF,0xFF,0xFF,0xFE,0xFF,0xFE], true);
    public static bool G1IsInSubGroup(ReadOnlySpan<byte> bytes)
    {
        return true;
    }

    public static bool G2IsInSubGroup(ReadOnlySpan<byte> bytes)
    {
        return true;
    }
}
