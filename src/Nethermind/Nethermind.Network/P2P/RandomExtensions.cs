// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Network.P2P
{
    internal static class RandomExtensions
    {
        public static long NextLong(this Random random) => ((long)random.Next() << 32) | (long)random.Next();
    }
}
