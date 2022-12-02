// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Microsoft.Extensions.Logging;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Eth1Bridge
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
                new EventId(1350, nameof(PeeringWorkerStarting)),
                "Eth1 bridge {ProductTokenVersion} worker starting; {Environment} environment [{ThreadId}]");

        public static readonly Action<ILogger, Bytes32, ulong, uint, int, Exception?> Eth1GenesisSuccess =
            LoggerMessage.Define<Bytes32, ulong, uint, int>(LogLevel.Information,
                new EventId(1351, nameof(Eth1GenesisSuccess)),
                "Eth genesis succeeded with block hash {BlockHash}, genesis time {GenesisTime:n0}, and {DepositCount} deposits, at check {CheckGenesisCount}.");

        // 2bxx 

        // 4bxx warning

        public static readonly Action<ILogger, ulong, ulong, ulong, Exception?> QuickStartEth1TimestampTooLow =
            LoggerMessage.Define<ulong, ulong, ulong>(LogLevel.Warning,
                new EventId(4390, nameof(QuickStartEth1TimestampTooLow)),
                "Quick start Eth1Timestamp {ConfiguredEth1Timestamp} to low for genesis {Genesis}; using {MinimumEth1Timestamp}.");
        public static readonly Action<ILogger, ulong, ulong, ulong, Exception?> QuickStartEth1TimestampTooHigh =
            LoggerMessage.Define<ulong, ulong, ulong>(LogLevel.Warning,
                new EventId(4391, nameof(QuickStartEth1TimestampTooHigh)),
                "Quick start Eth1Timestamp {ConfiguredEth1Timestamp} to high for genesis {Genesis}; using {MaximumEth1Timestamp}.");

        public static readonly Action<ILogger, ulong, ulong, Exception?> MockedQuickStart =
            LoggerMessage.Define<ulong, ulong>(LogLevel.Warning,
                new EventId(4900, nameof(MockedQuickStart)),
                "Mocked quick start with genesis time {GenesisTime:n0} and {ValidatorCount} validators.");

        // 5bxx error

        // 8bxx finalization

        // 9bxx critical
    }
}
