/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using Nethermind.Config;

namespace Nethermind.Network.Config
{
    public interface INetworkConfig : IConfig
    {
        /// <summary>
        /// List of trusted nodes - we connect to them and set predefined high reputation
        /// </summary>
        string TrustedPeers { get; set; }
        
        /// <summary>
        /// List of static nodes - we try to keep connection on all the time
        /// </summary>
        string StaticPeers { get; set; }

        /// <summary>
        /// Base path for discovery db
        /// </summary>
        string DbBasePath { get; set; } // TODO: move from Network config

        /// <summary>
        /// On/Off for peers
        /// </summary>
        bool IsPeersPersistenceOn { get; set; }

        /// <summary>
        /// Max amount of active peers on the tcp level 
        /// </summary>
        int ActivePeersMaxCount { get; }

        /// <summary>
        /// Time between persisting peers in milliseconds
        /// </summary>
        int PeersPersistenceInterval { get; set; }
        
        /// <summary>
        /// Time between persisting peers in milliseconds
        /// </summary>
        int PeersUpdateInterval { get; set; }

        /// <summary>
        /// Time between sending p2p ping
        /// </summary>
        int P2PPingInterval { get; }

        /// <summary>
        /// Number of ping missed for disconnection
        /// </summary>
        int P2PPingRetryCount { get; }

        /// <summary>
        /// Max Persisted Peer count
        /// </summary>
        int MaxPersistedPeerCount { get; }
        
        /// <summary>
        /// Persisted Peer Count Cleanup Threshold
        /// </summary>
        int PersistedPeerCountCleanupThreshold { get; set; }
        
        /// <summary>
        /// Max Candidate Peer count
        /// </summary>
        int MaxCandidatePeerCount { get; set; }
        
        /// <summary>
        /// Candidate Peer Count Cleanup Threshold
        /// </summary>
        int CandidatePeerCountCleanupThreshold { get; set; }
    }
}