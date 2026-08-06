// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.State;

namespace Nethermind.Network.P2P.Subprotocols.NHist.Messages;

public class HistoryRangeAtHeightMessage : NHistMessageBase
{
    public override int PacketType => NHist1MessageCode.HistoryRangeAtHeight;

    public IOwnedReadOnlyList<HistoryRangeEntry> Entries { get; set; } = ArrayPoolList<HistoryRangeEntry>.Empty();

    public byte[]? NextCursor { get; set; }

    public override void Dispose()
    {
        base.Dispose();
        Entries.Dispose();
    }
}
