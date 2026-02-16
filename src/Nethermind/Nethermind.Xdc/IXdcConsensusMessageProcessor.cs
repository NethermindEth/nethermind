// SPDX-FileCopyrightText: 2026 Anil Chinchawale
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Xdc.Types;

namespace Nethermind.Xdc
{
    /// <summary>
    /// Interface for processing XDPoS v2 consensus messages received via P2P
    /// </summary>
    public interface IXdcConsensusMessageProcessor
    {
        /// <summary>
        /// Process a validator vote received from a peer
        /// </summary>
        void ProcessVote(Vote vote);

        /// <summary>
        /// Process a timeout certificate received from a peer
        /// </summary>
        void ProcessTimeout(Timeout timeout);

        /// <summary>
        /// Process sync info (QC/TC) received from a peer
        /// </summary>
        void ProcessSyncInfo(SyncInfo syncInfo);

        /// <summary>
        /// Process a quorum certificate received from a peer
        /// </summary>
        void ProcessQuorumCertificate(QuorumCertificate qc);
    }
}
