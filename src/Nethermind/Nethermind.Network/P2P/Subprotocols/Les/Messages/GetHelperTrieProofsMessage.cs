// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Les.Messages
{
    public class GetHelperTrieProofsMessage : P2PMessage
    {
        public override int PacketType { get; } = LesMessageCode.GetHelperTrieProofs;
        public override string Protocol { get; } = Contract.P2P.Protocol.Les;
        public long RequestId;
        public HelperTrieRequest[] Requests;
    }
}
