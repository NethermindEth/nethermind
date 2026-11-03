// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using Paprika.Crypto;

namespace Paprika.Tests;

public static class Values
{
    public static readonly Keccak Key0 =
        new(Bytes.FromHexString("0x0001020304050607000102030405060700010203040506070001020304050607"));

    public static readonly Keccak Key1A =
        new(Bytes.FromHexString("0x0101020304050607000102030405060700010203040506070001020304050607"));

    public static readonly Keccak Key1 =
        new(Bytes.FromHexString("0x0101020304050607000102030405060700010203040506070001020304050607"));

    public static readonly Keccak Key1B =
        new(Bytes.FromHexString("0x0101020304050607000102030405060700010203040506070001020304050608"));

    public static readonly Keccak Key2 =
        new(Bytes.FromHexString("0x0606060606060606060606060606060606060606060606060606060606060606"));

    public static readonly Keccak Key3 = Keccak.Compute(Bytes.FromHexString("0xea2217"));
    public static readonly Keccak Key4 = Keccak.Compute(Bytes.FromHexString("0x010203"));
    public static readonly Keccak Key5 = Keccak.Compute(Bytes.FromHexString("0x23ea21"));
}
