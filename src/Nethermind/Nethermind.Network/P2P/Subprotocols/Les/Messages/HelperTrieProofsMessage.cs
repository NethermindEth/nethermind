// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Les.Messages
{
    public class HelperTrieProofsMessage : P2PMessage
    {
        public override int PacketType { get; } = LesMessageCode.HelperTrieProofs;
        public override string Protocol { get; } = Contract.P2P.Protocol.Les;
        public long RequestId;
        public int BufferValue;

        public byte[][] ProofNodes;
        public byte[][] AuxiliaryData;

        public HelperTrieProofsMessage()
        {
        }

        public HelperTrieProofsMessage(byte[][] proofNodes, byte[][] auxiliaryData, long requestId, int bufferValue)
        {
            ProofNodes = proofNodes;
            AuxiliaryData = auxiliaryData;
            RequestId = requestId;
            BufferValue = bufferValue;
        }
    }
}
