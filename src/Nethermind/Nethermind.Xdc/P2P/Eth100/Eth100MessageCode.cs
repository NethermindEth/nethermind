// SPDX-FileCopyrightText: 2026 Anil Chinchawale
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Xdc.P2P.Eth100
{
    /// <summary>
    /// XDC Network eth/100 protocol message codes
    /// Extends standard Ethereum eth/63 with XDPoS v2 consensus messages
    /// </summary>
    public static class Eth100MessageCode
    {
        // Standard eth/63 messages (0x00-0x10) handled by base class
        
        // XDPoS v2 specific messages (0x11-0x14)
        public const int Vote = 0x11;              // Validator vote for proposed block
        public const int Timeout = 0x12;           // Round timeout certificate
        public const int SyncInfo = 0x13;          // Consensus state synchronization
        public const int QuorumCertificate = 0x14; // BFT finality proof
    }
}
