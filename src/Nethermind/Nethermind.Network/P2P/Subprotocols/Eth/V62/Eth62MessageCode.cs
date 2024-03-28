// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62
{
    public static class Eth62MessageCode
    {
        public const int Status = 0x00;
        public const int NewBlockHashes = 0x01;
        public const int Transactions = 0x02;
        public const int GetBlockHeaders = 0x03;
        public const int BlockHeaders = 0x04;
        public const int GetBlockBodies = 0x05;
        public const int BlockBodies = 0x06;
        public const int NewBlock = 0x07;
    }
}
