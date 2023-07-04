// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.P2P.Subprotocols.Eth.V63
{
    public static class Eth63MessageCode
    {
        public const int GetNodeData = 0x0d;
        public const int NodeData = 0x0e;
        public const int GetReceipts = 0x0f;
        public const int Receipts = 0x10;
    }
}
