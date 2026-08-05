// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.P2P.Subprotocols.NHist.Messages;

public class GetChangesetsMessage : NHistMessageBase
{
    public override int PacketType => NHist1MessageCode.GetChangesets;

    public ulong FromBlock { get; set; }

    public ulong ToBlock { get; set; }

    public long ResponseBytes { get; set; }
}
