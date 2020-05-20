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

namespace Nethermind.BeaconNode.OApi
{
    internal static class LogDebug
    {
        // 64xx debug - validator
        
        public static readonly Action<ILogger, Exception?> NodeGenesisTimeRequested =
            LoggerMessage.Define(LogLevel.Debug,
                new EventId(6480, nameof(NodeGenesisTimeRequested)),
                "Node genesis time requested.");
        
        public static readonly Action<ILogger, Exception?> NodeForkRequested =
            LoggerMessage.Define(LogLevel.Debug,
                new EventId(6481, nameof(NodeForkRequested)),
                "Node fork requested.");
        
        public static readonly Action<ILogger, Exception?> NodeSyncingRequested =
            LoggerMessage.Define(LogLevel.Debug,
                new EventId(6482, nameof(NodeSyncingRequested)),
                "Node syncing status requested.");
    }
}