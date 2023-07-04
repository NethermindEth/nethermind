// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.Core
{
    public static class Unit
    {
        public static UInt256 Wei = 1;
        public static UInt256 GWei = 1_000_000_000;
        public static UInt256 Szabo = 1_000_000_000_000;
        public static UInt256 Finney = 1_000_000_000_000_000;
        public static UInt256 Ether = 1_000_000_000_000_000_000;
        public const string EthSymbol = "Ξ";
    }
}
