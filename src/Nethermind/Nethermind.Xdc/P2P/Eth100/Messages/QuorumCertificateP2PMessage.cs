// SPDX-FileCopyrightText: 2026 Anil Chinchawale
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.P2P.Messages;
using Nethermind.Xdc.Types;
using QuorumCertificate = Nethermind.Xdc.Types.QuorumCertificate;

namespace Nethermind.Xdc.P2P.Eth100.Messages
{
    /// <summary>
    /// P2P message wrapper for XDPoS v2 QuorumCertificate
    /// Propagates BFT finality proofs across the network
    /// </summary>
    public class QuorumCertificateP2PMessage : P2PMessage
    {
        public override int PacketType => Eth100MessageCode.QuorumCertificate;
        public override string Protocol => "eth";

        public QuorumCertificate QuorumCertificate { get; set; }

        public QuorumCertificateP2PMessage(QuorumCertificate qc)
        {
            QuorumCertificate = qc;
        }

        public QuorumCertificateP2PMessage()
        {
        }

        public override string ToString() => 
            $"{nameof(QuorumCertificateP2PMessage)}({QuorumCertificate?.ProposedBlockInfo})";
    }
}
