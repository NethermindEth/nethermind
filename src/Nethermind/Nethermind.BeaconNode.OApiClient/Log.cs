// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
