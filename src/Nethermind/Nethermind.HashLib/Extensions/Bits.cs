// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;

namespace Nethermind.HashLib.Extensions
{
    internal static class Bits
    {
        static public uint RotateLeft(uint a_uint, int a_n)
        {
            Debug.Assert(a_n >= 0);

            return (uint)((a_uint << a_n) | (a_uint >> (32 - a_n)));
        }

        static public uint RotateRight(uint a_uint, int a_n)
        {
            Debug.Assert(a_n >= 0);

            return (uint)((a_uint >> a_n) | (a_uint << (32 - a_n)));
        }

        static public ulong RotateRight(ulong a_ulong, int a_n)
        {
            Debug.Assert(a_n >= 0);

            return (ulong)((a_ulong >> a_n) | (a_ulong << (64 - a_n)));
        }
    }
}
