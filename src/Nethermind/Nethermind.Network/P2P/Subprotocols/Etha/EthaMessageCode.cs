// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.P2P.Subprotocols.Eth.V62;
using Nethermind.Network.P2P.Subprotocols.Eth.V63;


namespace Nethermind.Network.P2P.Subprotocols.Etha
{
    public static class EthaMessageCode
    {
        public const int Status = Eth62MessageCode.Status; // 0x00
        public const int GetBlockBodies = Eth62MessageCode.GetBlockBodies; // 0x05
        public const int BlockBodies = Eth62MessageCode.BlockBodies; // 0x06
        public const int GetReceipts = Eth63MessageCode.GetReceipts; // 0x0f
        public const int Receipts = Eth63MessageCode.Receipts; // 0x10
    }
}
