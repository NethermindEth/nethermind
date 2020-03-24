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
                "Peering worker execute running.");
        
        public static readonly Action<ILogger, Exception?> PeeringWorkerStarted =
            LoggerMessage.Define(LogLevel.Debug,
                new EventId(6051, nameof(PeeringWorkerStarted)),
                "Peering worker started.");
        
        public static readonly Action<ILogger, Exception?> PeeringWorkerStopping =
            LoggerMessage.Define(LogLevel.Debug,
                new EventId(6052, nameof(PeeringWorkerStopping)),
                "Peering worker stopping.");
        
        public static readonly Action<ILogger, string, int, Exception?> GossipReceived =
            LoggerMessage.Define<string, int>(LogLevel.Debug,
                new EventId(6053, nameof(GossipReceived)),
                "Gossip received, topic '{Topic}', {ByteCount} bytes.");
        
        public static readonly Action<ILogger, bool, string, string, int, Exception?> RpcReceived =
            LoggerMessage.Define<bool, string, string, int>(LogLevel.Debug,
                new EventId(6054, nameof(RpcReceived)),
                "RPC (response {IsResponse}) received, method '{Method}', peer {Peer}, {ByteCount} bytes.");
        
        public static readonly Action<ILogger, string, int, Exception?> GossipSend =
            LoggerMessage.Define<string, int>(LogLevel.Debug,
                new EventId(6055, nameof(GossipSend)),
                "Gossip send, topic '{Topic}', {ByteCount} bytes.");
        
        public static readonly Action<ILogger, string?, int?, int, Exception?> MothraStarting =
            LoggerMessage.Define<string?, int?, int>(LogLevel.Debug,
                new EventId(6056, nameof(MothraStarting)),
                "Starting MothraLibp2p on {MothraAddress} port {MothraPort} with {MothraBootNodeCount} boot nodes.");
        
        public static readonly Action<ILogger, string, string, Exception?> CreatingMothraLogDirectory =
            LoggerMessage.Define<string, string>(LogLevel.Debug,
                new EventId(6057, nameof(CreatingMothraLogDirectory)),
                "Creating Mothra log directory {LogDirectoryName} in {MothraBasePath}.");
    }
}