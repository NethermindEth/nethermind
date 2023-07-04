// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.P2P.Subprotocols.Eth.V65
{
    public static class Eth65MessageCode
    {
        public const int NewPooledTransactionHashes = 0x08;
        public const int GetPooledTransactions = 0x09;
        public const int PooledTransactions = 0x0a;
    }
}
