// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Microsoft.Extensions.Logging;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Eth1Bridge
{
    internal static class LogDebug
    {
        // 6bxx debug

        // 635x debug - eth1 (Eth1Bridge)
        public static readonly Action<ILogger, Exception?> PeeringWorkerExecute =
            LoggerMessage.Define(LogLevel.Debug,
                new EventId(6350, nameof(PeeringWorkerExecute)),
                "Eth1 bridge worker execute running.");

        public static readonly Action<ILogger, Exception?> PeeringWorkerStopping =
            LoggerMessage.Define(LogLevel.Debug,
                new EventId(6352, nameof(PeeringWorkerStopping)),
                "Eth1 bridge worker stopping.");

        public static readonly Action<ILogger, int, Exception?> CheckingForEth1Genesis =
            LoggerMessage.Define<int>(LogLevel.Debug,
                new EventId(6353, nameof(CheckingForEth1Genesis)),
                "Checking for Eth1 genesis {CheckGenesisCount}.");

        // 7bxx - mock

        public static readonly Action<ILogger, Bytes32, ulong, uint, Exception?> QuickStartGenesisDataCreated =
            LoggerMessage.Define<Bytes32, ulong, uint>(LogLevel.Debug,
                new EventId(7300, nameof(QuickStartGenesisDataCreated)),
                "Quick start genesis data created with block hash {BlockHash}, genesis time {GenesisTime:n0}, and {DepositCount} deposits.");
        public static readonly Action<ILogger, ValidatorIndex, string, Exception?> QuickStartAddValidator =
            LoggerMessage.Define<ValidatorIndex, string>(LogLevel.Debug,
                new EventId(7301, nameof(QuickStartAddValidator)),
                "Quick start adding deposit for mocked validator {ValidatorIndex} with public key {PublicKey}.");

    }
}
