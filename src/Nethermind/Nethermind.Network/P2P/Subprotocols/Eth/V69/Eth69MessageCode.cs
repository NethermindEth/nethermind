// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.P2P.Subprotocols.Eth.V62;
using Nethermind.Network.P2P.Subprotocols.Eth.V63;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V69;

public static class Eth69MessageCode
{
    public const int Status = Eth62MessageCode.Status;
    public const int GetReceipts = Eth63MessageCode.GetReceipts;
    public const int Receipts = Eth63MessageCode.Receipts;
    public const int BlockRangeUpdate = 0x11;
}
