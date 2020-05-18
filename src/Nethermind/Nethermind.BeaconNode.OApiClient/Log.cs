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

namespace Nethermind.BeaconNode.OApiClient
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

        public static readonly Action<ILogger, string, int, Exception?> NodeConnectionSuccess =
            LoggerMessage.Define<string, int>(LogLevel.Warning,
                new EventId(1491, nameof(NodeConnectionSuccess)),
                "Connected to node '{NodeUrl}' (index {NodeUrlIndex}).");
        
        // 4bxx warning

        public static readonly Action<ILogger, string, Exception?> NodeConnectionFailed =
            LoggerMessage.Define<string>(LogLevel.Warning,
                new EventId(4490, nameof(NodeConnectionFailed)),
                "Connection to '{NodeUrl}' failed. Attempting reconnection.");
        
        public static readonly Action<ILogger, int, int, Exception?> AllNodeConnectionsFailing =
            LoggerMessage.Define<int, int>(LogLevel.Warning,
                new EventId(4491, nameof(AllNodeConnectionsFailing)),
                "All node connections failing (configured with {NodeUrlCount} URLs). Waiting {MillisecondsDelay} milliseconds before attempting reconnection.");

        public static readonly Action<ILogger, Uri?, HttpStatusCode?, string?, TimeSpan, int, Exception?> NodeConnectionRetry =
            LoggerMessage.Define<Uri?, HttpStatusCode?, string?, TimeSpan, int>(LogLevel.Warning,
                new EventId(4492, nameof(NodeConnectionRetry)),
                "Connection to {RequestUri} failed with status {StatusCode} {ReasonPhrase}, delaying for {DelayTime}, then attempting reconnection {RetryCount}.");
    }
}