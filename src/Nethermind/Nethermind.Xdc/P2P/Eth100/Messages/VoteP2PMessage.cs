// SPDX-FileCopyrightText: 2026 Anil Chinchawale
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.P2P.Messages;
using Nethermind.Xdc.Types;

namespace Nethermind.Xdc.P2P.Eth100.Messages
{
    /// <summary>
    /// P2P message wrapper for XDPoS v2 Vote
    /// Propagates validator votes for proposed blocks across the network
    /// </summary>
    public class VoteP2PMessage : P2PMessage
    {
        public override int PacketType => Eth100MessageCode.Vote;
        public override string Protocol => "eth";

        public Vote Vote { get; set; }

        public VoteP2PMessage(Vote vote)
        {
            Vote = vote;
        }

        public VoteP2PMessage()
        {
        }

        public override string ToString() => $"{nameof(VoteP2PMessage)}({Vote})";
    }
}
