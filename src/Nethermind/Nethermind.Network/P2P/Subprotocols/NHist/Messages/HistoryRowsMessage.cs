// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.State;

namespace Nethermind.Network.P2P.Subprotocols.NHist.Messages;

public class HistoryRowsMessage : NHistMessageBase
{
    public override int PacketType => NHist1MessageCode.HistoryRows;

    public bool Refused { get; set; }

    public IOwnedReadOnlyList<HistoryRowEntry> Entries { get; set; } = ArrayPoolList<HistoryRowEntry>.Empty();

    public byte[]? NextCursor { get; set; }

    public override void Dispose()
    {
        base.Dispose();
        Entries.Dispose();
    }
}
