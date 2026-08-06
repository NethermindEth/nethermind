// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State;

namespace Nethermind.Network.P2P.Subprotocols.NHist.Messages;

public class NHistStatusMessage : NHistMessageBase
{
    public override int PacketType => NHist1MessageCode.Status;

    public HistoryServingScope[] Scopes { get; set; } = [];
}
