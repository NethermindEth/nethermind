// SPDX-FileCopyrightText: 2026 Anil Chinchawale
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Xdc.P2P.Eth100
{
    /// <summary>
    /// XDC Network eth/100 protocol message codes
    /// Extends standard Ethereum eth/63 with XDPoS v2 consensus messages
    /// 
    /// IMPORTANT: geth-xdc uses 0xe0-0xe2 for consensus messages (NOT 0x11-0x14)
    /// This requires MessageIdSpaceSize = 227 (0xe2 + 1)
    /// Reference: XDC geth eth/protocols/xdcHandler.go
    /// </summary>
    public static class Eth100MessageCode
    {
        // Standard eth/63 messages (0x00-0x10) handled by base class
        
        // XDPoS v2 consensus messages — must match geth-xdc codes
        public const int Vote = 0xe0;              // 224 - Validator vote for proposed block
        public const int Timeout = 0xe1;           // 225 - Round timeout certificate
        public const int SyncInfo = 0xe2;          // 226 - Consensus state synchronization
        
        // Note: geth-xdc only has 3 consensus messages (Vote, Timeout, SyncInfo)
        // QuorumCertificate is embedded inside Vote/Timeout/SyncInfo, not a standalone message
    }
}
