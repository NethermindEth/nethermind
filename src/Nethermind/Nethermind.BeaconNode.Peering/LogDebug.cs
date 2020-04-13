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
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.P2p;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Peering
{
    internal static class LogDebug
    { 
        // 6bxx debug

        // 605x debug - peering (Mothra)
        public static readonly Action<ILogger, Exception?> PeeringWorkerExecute =
            LoggerMessage.Define(LogLevel.Debug,
                new EventId(6050, nameof(PeeringWorkerExecute)),
                "Peering worker execute running, awaiting store to be initialised with anchor state.");
        
        public static readonly Action<ILogger, Exception?> PeeringWorkerExecuteCompleted =
            LoggerMessage.Define(LogLevel.Debug,
                new EventId(6051, nameof(PeeringWorkerExecuteCompleted)),
                "Peering worker execute completed, peering now running.");
        
        public static readonly Action<ILogger, Exception?> PeeringWorkerStopping =
            LoggerMessage.Define(LogLevel.Debug,
                new EventId(6052, nameof(PeeringWorkerStopping)),
                "Peering worker stopping.");
        
        public static readonly Action<ILogger, string, int, Exception?> GossipReceived =
            LoggerMessage.Define<string, int>(LogLevel.Debug,
                new EventId(6053, nameof(GossipReceived)),
                "Gossip received, topic '{Topic}', {ByteCount} bytes.");
        
        public static readonly Action<ILogger, RpcDirection, int, string, string, int, string, Exception?> RpcReceived =
            LoggerMessage.Define<RpcDirection, int, string, string, int, string>(LogLevel.Debug,
                new EventId(6054, nameof(RpcReceived)),
                "RPC {RpcDirection} ({RequestResponseFlag}) received, method '{Method}', peer {Peer}, {ByteCount} bytes, processed as {ProcessAsMethod}.");
        
        public static readonly Action<ILogger, string, int, Exception?> GossipSend =
            LoggerMessage.Define<string, int>(LogLevel.Debug,
                new EventId(6055, nameof(GossipSend)),
                "Gossip send, topic {Topic}, {ByteCount} bytes.");
        
        public static readonly Action<ILogger, string?, int?, int, Exception?> MothraStarting =
            LoggerMessage.Define<string?, int?, int>(LogLevel.Debug,
                new EventId(6056, nameof(MothraStarting)),
                "Starting MothraLibp2p on {MothraAddress} port {MothraPort} with {MothraBootNodeCount} boot nodes.");
        
        public static readonly Action<ILogger, string, string, Exception?> CreatingMothraLogDirectory =
            LoggerMessage.Define<string, string>(LogLevel.Debug,
                new EventId(6057, nameof(CreatingMothraLogDirectory)),
                "Creating Mothra log directory {LogDirectoryName} in {MothraBasePath}.");

        public static readonly Action<ILogger, RpcDirection, string, string, int, Exception?> RpcSend =
            LoggerMessage.Define<RpcDirection, string, string, int>(LogLevel.Debug,
                new EventId(6058, nameof(RpcSend)),
                "RPC send {RpcDirection}, method {Method}, peer {Peer}, {ByteCount} bytes.");
        
        public static readonly Action<ILogger, string, Exception?> AddingExpectedPeer =
            LoggerMessage.Define<string>(LogLevel.Debug,
                new EventId(6059, nameof(AddingExpectedPeer)),
                "Adding expected peer with ENR {PeerEnr}.");
        
        public static readonly Action<ILogger, string, Guid, ConnectionDirection, Exception?> CreatedPeerSession =
            LoggerMessage.Define<string, Guid, ConnectionDirection>(LogLevel.Debug,
                new EventId(6060, nameof(CreatedPeerSession)),
                "Created peer {Peer} session {Session} with direction {ConnectionDirection}.");
        
        public static readonly Action<ILogger, Exception?> StoreInitializedStartingPeering =
            LoggerMessage.Define(LogLevel.Debug,
                new EventId(6061, nameof(StoreInitializedStartingPeering)),
                "Store initialized, peering worker starting peer-to-peer.");
        
        public static readonly Action<ILogger, ulong, Exception?> PeeringWaitingForAnchorState =
            LoggerMessage.Define<ulong>(LogLevel.Debug,
                new EventId(6062, nameof(PeeringWaitingForAnchorState)),
                "Store not initialized, waiting for anchor state (waiting {WaitSeconds} seconds).");
        
        public static readonly Action<ILogger, string, Guid, ConnectionDirection, Exception?> OpenedPeerSession =
            LoggerMessage.Define<string, Guid, ConnectionDirection>(LogLevel.Debug,
                new EventId(6063, nameof(CreatedPeerSession)),
                "Opened peer {Peer} session {Session} with direction {ConnectionDirection}.");
        
        public static readonly Action<ILogger, string, Guid, ConnectionDirection, Exception?> DisconnectingPeerSession =
            LoggerMessage.Define<string, Guid, ConnectionDirection>(LogLevel.Debug,
                new EventId(6064, nameof(CreatedPeerSession)),
                "Disconnecting peer {Peer} session {Session} with direction {ConnectionDirection}.");
        
        public static readonly Action<ILogger, BeaconBlock, string, Exception?> ProcessSignedBeaconBlock =
            LoggerMessage.Define<BeaconBlock, string>(LogLevel.Debug,
                new EventId(6065, nameof(ProcessSignedBeaconBlock)),
                "Processing signed beacon block, {BeaconBlock}, from {BlockSource}");
        
        public static readonly Action<ILogger, string, Exception?> ProcessPeerDiscovered =
            LoggerMessage.Define<string>(LogLevel.Debug,
                new EventId(6066, nameof(ProcessPeerDiscovered)),
                "Processing peer discovered, {PeerId}");

        public static readonly Action<ILogger, BeaconBlocksByRange, Exception?> ProcessBeaconBlocksByRange =
            LoggerMessage.Define<BeaconBlocksByRange>(LogLevel.Debug,
                new EventId(6078, nameof(ProcessBeaconBlocksByRange)),
                "Processing beacon blocks by range request {BlockRoot}.");

        public static readonly Action<ILogger, BeaconBlock, Root, Exception?> SendingRequestBlocksByRangeResponse =
            LoggerMessage.Define<BeaconBlock, Root>(LogLevel.Debug,
                new EventId(6079, nameof(SendingRequestBlocksByRangeResponse)),
                "Sending (signed) beacon block {BeaconBlock} with root {BlockRoot}.");
    }
}