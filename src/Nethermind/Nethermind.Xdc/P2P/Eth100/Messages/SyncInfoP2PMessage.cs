// SPDX-FileCopyrightText: 2026 Anil Chinchawale
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.P2P.Messages;
using Nethermind.Xdc.Types;

namespace Nethermind.Xdc.P2P.Eth100.Messages
{
    /// <summary>
    /// P2P message wrapper for XDPoS v2 SyncInfo
    /// Synchronizes consensus state (QC, TC) between peers
    /// </summary>
    public class SyncInfoP2PMessage : P2PMessage
    {
        public override int PacketType => Eth100MessageCode.SyncInfo;
        public override string Protocol => "eth";

        public SyncInfo SyncInfo { get; set; }

        public SyncInfoP2PMessage(SyncInfo syncInfo)
        {
            SyncInfo = syncInfo;
        }

        public SyncInfoP2PMessage()
        {
        }

        public override string ToString() => 
            $"{nameof(SyncInfoP2PMessage)}(QC:{SyncInfo?.HighestQuorumCert?.ProposedBlockInfo}, TC:{SyncInfo?.HighestTimeoutCert?.Round})";
    }
}
