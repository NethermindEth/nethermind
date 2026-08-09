// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State;

namespace Nethermind.Network.P2P.Subprotocols.NHist.Messages;

public class GetHistoryRowsMessage : NHistMessageBase
{
    public override int PacketType => NHist1MessageCode.GetHistoryRows;

    public HistoryRowColumn Column { get; set; }

    public byte[] StartKey { get; set; } = [];

    public byte[] EndKey { get; set; } = [];

    public byte[] Cursor { get; set; } = [];

    public long ResponseBytes { get; set; }
}
