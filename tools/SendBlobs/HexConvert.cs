// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace SendBlobs;
internal static class HexConvert
{
    public static UInt256 ToUInt256(string s)
    {
        return new UInt256(Bytes.FromHexString(s), isBigEndian: true);
    }

    public static ulong ToUInt64(string s)
    {
        return Convert.ToUInt64(s, s.StartsWith("0x") ? 16 : 10);
    }
}
