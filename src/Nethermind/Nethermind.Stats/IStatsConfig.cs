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
using Nethermind.Stats.Model;

namespace Nethermind.Stats
{
    public interface IStatsConfig : IConfig
    {
        /// <summary>
        /// Whether we should capture Node Stats events history
        /// </summary>
        bool CaptureNodeStatsEventHistory { get; set; }

        /// <summary>
        /// Whether we should capture Node Latency Stats events history
        /// </summary>
        bool CaptureNodeLatencyStatsEventHistory { get; set; }

        /// <summary>
        /// Value of predefined reputation for trusted nodes
        /// </summary>
        long PredefinedReputation { get; }

        /// <summary>
        /// Local disconnect reasons for penalizing node reputation
        /// </summary>
        DisconnectReason[] PenalizedReputationLocalDisconnectReasons { get; }

        /// <summary>
        /// Remote disconnect reasons for penalizing node reputation
        /// </summary>
        DisconnectReason[] PenalizedReputationRemoteDisconnectReasons { get; }

        /// <summary>
        /// Time within which we penalized peer if disconnection happends due to too many peers
        /// </summary>
        long PenalizedReputationTooManyPeersTimeout { get; }
        
        /// <summary>
        /// Failed connection delays - last entry is used for all further events
        /// </summary>
        int[] FailedConnectionDelays { get; }
        
        /// <summary>
        /// Disconnect delays - last entry is used for all further events
        /// </summary>
        int[] DisconnectDelays { get; }
    }
}