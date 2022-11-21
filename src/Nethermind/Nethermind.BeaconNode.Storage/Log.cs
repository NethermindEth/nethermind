// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Microsoft.Extensions.Logging;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Storage
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
        // a2xx fork choice, storage
        // a3xx deposit contract, Eth1, genesis
        // a4xx honest validator, API
        // a5xx custody game
        // a6xx shard data chains
        // a9xx miscellaneous / other

        // 1bxx preliminary

        public static readonly Action<ILogger, ulong, ulong, Checkpoint, Exception?> MemoryStoreInitialized =
            LoggerMessage.Define<ulong, ulong, Checkpoint>(LogLevel.Information,
                new EventId(1280, nameof(MemoryStoreInitialized)),
                "Memory store initialized at time {Time} with genesis {GenesisTime} and finalized checkpoint {FinalizedCheckpoint}");

        // 2bxx 

        // 4bxx warning

        // 5bxx error

        // 8bxx finalization

        // 9bxx critical

    }
}
