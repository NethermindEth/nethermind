// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace SendBlobs;
internal static class HexConvert
{
    public static ulong ToUInt64(string s)
    {
        return Convert.ToUInt64(s, s.StartsWith("0x") ? 16 : 10);
    }
}
