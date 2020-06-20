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
using Nethermind.Core2.P2p;
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
                "Peering {ProductTokenVersion} worker starting; {Environment} environment [{ThreadId}]");

        public static readonly Action<ILogger, string, Exception?> QueueProcessorExecuteStarting =
            LoggerMessage.Define<string>(LogLevel.Information,
                new EventId(1051, nameof(QueueProcessorExecuteStarting)),
                "Starting queue processor thread for {QueueProcessorName}");

        // 2bxx 
        
        public static readonly Action<ILogger, string, Exception?> PeerDiscovered =
            LoggerMessage.Define<string>(LogLevel.Information,
                new EventId(2050, nameof(PeerDiscovered)),
                "Peer discovered: {Peer}");

        // 4bxx warning
        
        public static readonly Action<ILogger, string, int, Exception?> UnknownGossipReceived =
            LoggerMessage.Define<string, int>(LogLevel.Warning,
                new EventId(4050, nameof(UnknownGossipReceived)),
                "Unknown gossip received, unknown topic '{Topic}', {ByteCount} bytes.");
        
        public static readonly Action<ILogger, RpcDirection, int, string, string, int, Exception?> UnknownRpcReceived =
            LoggerMessage.Define<RpcDirection, int, string, string, int>(LogLevel.Warning,
                new EventId(4051, nameof(UnknownRpcReceived)),
                "Unknown RPC {RpcDirection} ({RequestResponseFlag}) received, unknown method '{Method}', peer {Peer}, {ByteCount} bytes.");

        public static readonly Action<ILogger, string, Exception?> GossipNotPublishedAsPeeeringNotStarted =
            LoggerMessage.Define<string>(LogLevel.Warning,
                new EventId(4052, nameof(GossipNotPublishedAsPeeeringNotStarted)),
                "Gossip topic '{Topic}' not published as peering not started yet.");

        public static readonly Action<ILogger, string, Exception?> RpcRequestNotSentAsPeeeringNotStarted =
            LoggerMessage.Define<string>(LogLevel.Warning,
                new EventId(4053, nameof(RpcRequestNotSentAsPeeeringNotStarted)),
                "RPC request '{Method}' not sent as peering not started yet.");

        public static readonly Action<ILogger, string, Exception?> RpcResponseNotSentAsPeeeringNotStarted =
            LoggerMessage.Define<string>(LogLevel.Warning,
                new EventId(4054, nameof(RpcResponseNotSentAsPeeeringNotStarted)),
                "RPC response '{Method}' not sent as peering not started yet.");

        public static readonly Action<ILogger, Slot, Root, Exception?> RequestedBlockSkippedSlot =
            LoggerMessage.Define<Slot, Root>(LogLevel.Warning,
                new EventId(4055, nameof(RequestedBlockSkippedSlot)),
                "Requested block missing for slot {Slot} from head {HeadRoot} (may be skipped slot).");

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

        public static readonly Action<ILogger, BeaconBlock, string, Exception?> ProcessSignedBeaconBlockError =
            LoggerMessage.Define<BeaconBlock, string>(LogLevel.Error,
                new EventId(5053, nameof(ProcessSignedBeaconBlockError)),
                "Error handling signed beacon block, {BeaconBlock}: {ErrorMessage}");
        
        public static readonly Action<ILogger, string, string, Exception?> HandleRpcStatusError =
            LoggerMessage.Define<string, string>(LogLevel.Error,
                new EventId(5054, nameof(HandleRpcStatusError)),
                "Error handling status from peer {PeerId}: {ErrorMessage}");
        
        public static readonly Action<ILogger, string, string, Exception?> HandlePeerDiscoveredError =
            LoggerMessage.Define<string, string>(LogLevel.Error,
                new EventId(5055, nameof(HandlePeerDiscoveredError)),
                "Error handling peer discovered for {PeerId}: {ErrorMessage}");

        // 8bxx finalization

        // 9bxx critical

        public static readonly Action<ILogger, Exception?> PeeringWorkerCriticalError =
            LoggerMessage.Define(LogLevel.Critical,
                new EventId(9050, nameof(PeeringWorkerCriticalError)),
                "Critical unhandled error starting peering worker. Worker cannot continue.");
        
        public static readonly Action<ILogger, string, Exception?> QueueProcessorCriticalError =
            LoggerMessage.Define<string>(LogLevel.Critical,
                new EventId(9051, nameof(QueueProcessorCriticalError)),
                "Critical unhandled error in queue processor thread for {QueueProcessorName}. Processor cannot continue.");        
    }
}