//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

namespace Nethermind.Runner.Ethereum.Subsystems
{
    public enum EthereumSubsystem
    {
        /// <summary>
        /// JSON RPC system for web3 calls
        /// </summary>
        JsonRpc,
        /// <summary>
        /// Ethstats node monitoring service client
        /// </summary>
        EthStats,
        /// <summary>
        /// Prometheus / Grafana monitoring client for metrics
        /// </summary>
        Monitoring,
        /// <summary>
        /// Miner for block production / sealing
        /// </summary>
        Mining,
        /// <summary>
        /// Block processor to advance blockchain
        /// </summary>
        BlockProcessing,
        /// <summary>
        /// Peer manager for establishing peer connections
        /// </summary>
        Peering,
        /// <summary>
        /// Peer discovery process over UDP
        /// </summary>
        Discovery,
        /// <summary>
        /// Kafka publisher/subscriber topics
        /// </summary>
        Kafka,
        /// <summary>
        /// GRPC publisher/subscriber support
        /// </summary>
        Grpc,
        /// <summary>
        /// Wallet for signing messages
        /// </summary>
        Wallet,
        /// <summary>
        /// Nethermind Data Marketplace
        /// </summary>
        NDM
    }
}