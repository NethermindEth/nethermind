// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Jint.Parser.Ast;
using Nethermind.Int256;

namespace SendBlobs;
internal static class HexConvert
{
    public static UInt256 ToUInt256(string s)
    {
        return (UInt256)ToUInt64(s);
    }

    public static ulong ToUInt64(string s)
    {
        return Convert.ToUInt64(s, s.StartsWith("0x") ? 16 : 10);
    }
}
