// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Microsoft.Extensions.Logging;

namespace Nethermind.BeaconNode.OApiClient
{
    internal static class LogDebug
    {
        // 64xx debug - validator

        public static readonly Action<ILogger, string, int, Exception?> AttemptingConnectionToNode =
            LoggerMessage.Define<string, int>(LogLevel.Debug,
                new EventId(6494, nameof(AttemptingConnectionToNode)),
                "Attempting connection to node '{NodeUrl}' (index {NodeUrlIndex}).");

    }
}
