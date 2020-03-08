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

using System;
using Microsoft.Extensions.Logging;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Peering
{
    internal static class Log
    {
        // Event IDs: ABxx (based on Theory of Reply Codes)
        
        // Event ID Type:
        // 6bxx debug - general
        // 7bxx debug - test
        // 1bxx info - preliminary
        // 2bxx info - completion
        // 3bxx info - intermediate
        // 8bxx info - finalization
        // 4bxx warning
        // 5bxx error
        // 9bxx critical
        
        // Event ID Category:
        // a0xx core service, worker, configuration, peering
        // a1xx beacon chain, incl. state transition
        // a2xx fork choice
        // a3xx deposit contract, Eth1, genesis
        // a4xx honest validator, API
        // a5xx custody game
        // a6xx shard data chains
        // a9xx miscellaneous / other
        
        // 1bxx preliminary

        public static readonly Action<ILogger, string, string, int, Exception?> PeeringWorkerStarting =
            LoggerMessage.Define<string, string, int>(LogLevel.Information,
                new EventId(1050, nameof(PeeringWorkerStarting)),
                "Peering {ProductTokenVersion} worker started; {Environment} environment [{ThreadId}]");

        // 2bxx 
        
        public static readonly Action<ILogger, string, Exception?> PeerDiscovered =
            LoggerMessage.Define<string>(LogLevel.Information,
                new EventId(2050, nameof(PeerDiscovered)),
                "Peer discovered: {Peer}");

        // 4bxx warning
        
        // 5bxx error

        public static readonly Action<ILogger, string, string, Exception?> PeerDiscoveredError =
            LoggerMessage.Define<string, string>(LogLevel.Error,
                new EventId(5050, nameof(PeerDiscoveredError)),
                "Error processing peer discovered, peer '{Peer}': {ErrorMessage}");

        public static readonly Action<ILogger, string, string, Exception?> GossipReceivedError =
            LoggerMessage.Define<string, string>(LogLevel.Error,
                new EventId(5051, nameof(GossipReceivedError)),
                "Error processing gossip received, topic '{Topic}': {ErrorMessage}");

        public static readonly Action<ILogger, string, string, Exception?> RpcReceivedError =
            LoggerMessage.Define<string, string>(LogLevel.Error,
                new EventId(5052, nameof(RpcReceivedError)),
                "Peer error processing RPC method, '{Method}': {ErrorMessage}");

        public static readonly Action<ILogger, BeaconBlock, string, Exception?> HandleSignedBeaconBlockError =
            LoggerMessage.Define<BeaconBlock, string>(LogLevel.Error,
                new EventId(5053, nameof(HandleSignedBeaconBlockError)),
                "Error handling signed beacon block, {BeaconBlock}: {ErrorMessage}");

        // 8bxx finalization

        // 9bxx critical

        public static readonly Action<ILogger, Exception?> PeeringWorkerCriticalError =
            LoggerMessage.Define(LogLevel.Critical,
                new EventId(9050, nameof(PeeringWorkerCriticalError)),
                "Critical unhandled error starting peering worker. Worker cannot continue.");
    }
}