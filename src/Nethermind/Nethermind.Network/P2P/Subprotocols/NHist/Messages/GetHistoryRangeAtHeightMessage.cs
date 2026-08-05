// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Network.P2P.Subprotocols.NHist.Messages;

public class GetHistoryRangeAtHeightMessage : NHistMessageBase
{
    public override int PacketType => NHist1MessageCode.GetHistoryRangeAtHeight;

    public ValueHash256 StartKey { get; set; }

    public ValueHash256 EndKey { get; set; }

    public ulong Height { get; set; }

    public byte[] Cursor { get; set; } = [];

    public long ResponseBytes { get; set; }
}
