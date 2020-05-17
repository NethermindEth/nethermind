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
using System.Net;
using Microsoft.Extensions.Logging;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.OApi
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
        
        public static readonly Action<ILogger, IPAddress, Exception?> NodeVersionRequested =
            LoggerMessage.Define<IPAddress>(LogLevel.Information,
                new EventId(1480, nameof(NodeVersionRequested)),
                "Node version requested by client {RemoteIpAddress}.");

        // 2bxx completion

        public static readonly Action<ILogger, ulong, string, Exception?> NewBlockRequested =
            LoggerMessage.Define<ulong, string>(LogLevel.Information,
                new EventId(2480, nameof(NewBlockRequested)),
                "New block requested for slot {Slot} with RANDAO reveal {RandaoReveal}.");

        public static readonly Action<ILogger, Slot?, BlsSignature?, Root?, Root?, Bytes32?, BlsSignature?, Exception?>
            BlockPublished =
                LoggerMessage.Define<Slot?, BlsSignature?, Root?, Root?, Bytes32?, BlsSignature?>(LogLevel.Information,
                    new EventId(2481, nameof(BlockPublished)),
                    "Block received for slot {Slot} with RANDAO reveal {RandaoReveal}, parent {ParentRoot}, state root {StateRoot}, graffiti {Graffiti}, and signature {Signature}");

        // 4bxx warning

        // 5bxx error

        // 8bxx finalization

        // 9bxx critical
    }
}