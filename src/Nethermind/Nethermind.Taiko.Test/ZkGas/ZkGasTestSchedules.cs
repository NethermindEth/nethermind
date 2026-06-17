// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Taiko.ZkGas;

namespace Nethermind.Taiko.Test.ZkGas;

/// <summary>
/// Test-owned mirror of the Unzen ZK gas multiplier tables shipped in
/// <c>Chains/taiko-alethia.json</c> and <c>Chains/taiko-hoodi.json</c> under
/// <c>unzenZkGasSchedules</c>. Tests pin against these copies so a chainspec edit must also
/// update this file — drift surfaces as a failing assertion.
/// </summary>
public static class ZkGasTestSchedules
{
    private static readonly ushort[] _opcodes = BuildOpcodes();
    private static readonly FrozenDictionary<AddressAsKey, ushort> _precompiles = BuildPrecompiles();

    public static ReadOnlyMemory<ushort> OpcodeMultipliers => _opcodes;
    public static FrozenDictionary<AddressAsKey, ushort> PrecompileMultipliers => _precompiles;

    /// <summary>Convenience accessor for canonical EVM precompiles (single low byte → full address).</summary>
    public static ushort PrecompileMultiplier(byte canonicalLowByte) =>
        _precompiles[Address.FromNumber(canonicalLowByte)];

    private static ushort[] BuildOpcodes()
    {
        ushort[] a = new ushort[256];
        Array.Fill(a, ZkGasSchedule.FailsafeMultiplier);

        a[0x00] = 0;
        a[0x01] = 19;
        a[0x02] = 19;
        a[0x03] = 22;
        a[0x04] = 76;
        a[0x05] = 78;
        a[0x06] = 66;
        a[0x07] = 28;
        a[0x08] = 52;
        a[0x09] = 113;
        a[0x0a] = 21;
        a[0x0b] = 17;
        a[0x10] = 19;
        a[0x11] = 19;
        a[0x12] = 20;
        a[0x13] = 19;
        a[0x14] = 36;
        a[0x15] = 16;
        a[0x16] = 19;
        a[0x17] = 20;
        a[0x18] = 18;
        a[0x19] = 15;
        a[0x1a] = 17;
        a[0x1b] = 24;
        a[0x1c] = 22;
        a[0x1d] = 21;
        a[0x1e] = 14;
        a[0x20] = 31;
        a[0x30] = 19;
        a[0x31] = 4;
        a[0x32] = 21;
        a[0x33] = 18;
        a[0x34] = 11;
        a[0x35] = 22;
        a[0x36] = 13;
        a[0x37] = 13;
        a[0x38] = 11;
        a[0x39] = 12;
        a[0x3a] = 15;
        a[0x3b] = 4;
        a[0x3c] = 4;
        a[0x3d] = 12;
        a[0x3e] = 10;
        a[0x3f] = 7;
        a[0x40] = 6;
        a[0x41] = 18;
        a[0x42] = 10;
        a[0x43] = 12;
        a[0x44] = 42;
        a[0x45] = 13;
        a[0x46] = 11;
        a[0x47] = 52;
        a[0x48] = 14;
        a[0x49] = 13;
        a[0x4a] = 15;
        a[0x50] = 10;
        a[0x51] = 18;
        a[0x52] = 29;
        a[0x53] = 10;
        a[0x54] = 3;
        a[0x55] = 5;
        a[0x56] = 4;
        a[0x57] = 5;
        a[0x58] = 13;
        a[0x59] = 13;
        a[0x5a] = 11;
        a[0x5b] = 20;
        a[0x5c] = 1;
        a[0x5d] = 5;
        a[0x5e] = 4;
        a[0x5f] = 13;
        a[0x60] = 9;
        a[0x61] = 8;
        a[0x62] = 9;
        a[0x63] = 10;
        a[0x64] = 9;
        a[0x65] = 12;
        a[0x66] = 10;
        a[0x67] = 15;
        a[0x68] = 13;
        a[0x69] = 12;
        a[0x6a] = 12;
        a[0x6b] = 15;
        a[0x6c] = 15;
        a[0x6d] = 17;
        a[0x6e] = 21;
        a[0x6f] = 13;
        a[0x70] = 20;
        a[0x71] = 18;
        a[0x72] = 20;
        a[0x73] = 20;
        a[0x74] = 18;
        a[0x75] = 14;
        a[0x76] = 22;
        a[0x77] = 24;
        a[0x78] = 22;
        a[0x79] = 24;
        a[0x7a] = 16;
        a[0x7b] = 17;
        a[0x7c] = 28;
        a[0x7d] = 29;
        a[0x7e] = 16;
        a[0x7f] = 19;
        a[0x80] = 10;
        a[0x81] = 8;
        a[0x82] = 9;
        a[0x83] = 10;
        a[0x84] = 11;
        a[0x85] = 8;
        a[0x86] = 8;
        a[0x87] = 10;
        a[0x88] = 9;
        a[0x89] = 10;
        a[0x8a] = 10;
        a[0x8b] = 9;
        a[0x8c] = 9;
        a[0x8d] = 8;
        a[0x8e] = 8;
        a[0x8f] = 10;
        a[0x90] = 31;
        a[0x91] = 30;
        a[0x92] = 32;
        a[0x93] = 31;
        a[0x94] = 33;
        a[0x95] = 34;
        a[0x96] = 31;
        a[0x97] = 30;
        a[0x98] = 32;
        a[0x99] = 31;
        a[0x9a] = 33;
        a[0x9b] = 32;
        a[0x9c] = 31;
        a[0x9d] = 31;
        a[0x9e] = 36;
        a[0x9f] = 31;
        a[0xa0] = 3;
        a[0xa1] = 3;
        a[0xa2] = 2;
        a[0xa3] = 2;
        a[0xa4] = 2;
        a[0xf0] = 1;
        a[0xf1] = 20;
        a[0xf2] = 20;
        a[0xf3] = 0;
        a[0xf4] = 17;
        a[0xf5] = 1;
        a[0xfa] = 23;
        a[0xfd] = 0;
        a[0xfe] = 0;
        a[0xff] = 0;

        return a;
    }

    private static FrozenDictionary<AddressAsKey, ushort> BuildPrecompiles()
    {
        Dictionary<AddressAsKey, ushort> d = new()
        {
            [Address.FromNumber(0x01)] = 47,
            [Address.FromNumber(0x02)] = 10,
            [Address.FromNumber(0x03)] = 4,
            [Address.FromNumber(0x04)] = 6,
            [Address.FromNumber(0x05)] = 154,
            [Address.FromNumber(0x06)] = 19,
            [Address.FromNumber(0x07)] = 58,
            [Address.FromNumber(0x08)] = 54,
            [Address.FromNumber(0x09)] = 166,
            [Address.FromNumber(0x0a)] = 859,
            [Address.FromNumber(0x0b)] = 201,
            [Address.FromNumber(0x0c)] = 93,
            [Address.FromNumber(0x0d)] = 230,
            [Address.FromNumber(0x0e)] = 71,
            [Address.FromNumber(0x0f)] = 365,
            [Address.FromNumber(0x10)] = 246,
            [Address.FromNumber(0x11)] = 208,
            [Address.FromNumber(0x100)] = 163,
        };
        return d.ToFrozenDictionary();
    }
}
