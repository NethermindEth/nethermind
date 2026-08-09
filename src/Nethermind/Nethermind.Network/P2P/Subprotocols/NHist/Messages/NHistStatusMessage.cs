// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State;

namespace Nethermind.Network.P2P.Subprotocols.NHist.Messages;

public class NHistStatusMessage : NHistMessageBase
{
    public override int PacketType => NHist1MessageCode.Status;

    public HistoryServingScope[] Scopes { get; set; } = [];

    /// <summary>Whether the sender can serve a FULL archive clone (every row, from genesis/pivot) via
    /// <c>GetHistoryRows</c> - false for a windowed node, advertised up front so a clone importer picks a
    /// different feeder instead of discovering the refusal per request.</summary>
    public bool SupportsFullClone { get; set; }

    /// <summary>The on-disk row format (<see cref="IHistoryServer.RowFormatVersion"/>) a clone from the sender
    /// carries - no transcoding happens on the wire, so the importer must know this before requesting rows.</summary>
    public byte RowFormatVersion { get; set; }
}
