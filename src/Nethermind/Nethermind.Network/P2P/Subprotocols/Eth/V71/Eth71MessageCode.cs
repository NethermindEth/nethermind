// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.P2P.Subprotocols.Eth.V62;
using Nethermind.Network.P2P.Subprotocols.Eth.V63;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V71;

public static class Eth71MessageCode
{
    public const int Status = Eth62MessageCode.Status;
    public const int GetReceipts = Eth63MessageCode.GetReceipts;
    public const int Receipts = Eth63MessageCode.Receipts;
    public const int BlockRangeUpdate = 0x11;
    public const int GetBlockAccessLists = 0x12;
    public const int BlockAccessLists = 0x13;
}
