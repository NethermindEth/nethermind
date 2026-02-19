// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.P2P.Messages;
using Nethermind.Xdc.Types;

namespace Nethermind.Xdc.P2P;

internal class VoteMsg : P2PMessage
{
    public override int PacketType => Xdpos2MessageCode.VoteMsg;
    public override string Protocol => "eth";
    public Vote Vote { get; set; }
    public override string ToString() => $"{nameof(VoteMsg)}({Vote})";
}
