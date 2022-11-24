// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Microsoft.Extensions.Logging;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Storage
{
    internal static class LogDebug
    {
        // 6bxx debug

        // 628x debug - memory store

        public static readonly Action<ILogger, string, string, Exception?> CreatingMemoryStoreLogDirectory =
            LoggerMessage.Define<string, string>(LogLevel.Debug,
                new EventId(6280, nameof(CreatingMemoryStoreLogDirectory)),
                "Creating memory store log directory {LogDirectoryName} in {MemoryStoreBasePath}.");
    }
}
