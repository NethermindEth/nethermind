// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using MsLogging = Microsoft.Extensions.Logging;

namespace Nethermind.Logging.Microsoft
{
    public static class MicrosoftLoggerExtensions
    {
        public static bool IsError(this MsLogging.ILogger logger) => logger.IsEnabled(MsLogging.LogLevel.Error);

        public static bool IsWarn(this MsLogging.ILogger logger) => logger.IsEnabled(MsLogging.LogLevel.Warning);

        public static bool IsInfo(this MsLogging.ILogger logger) => logger.IsEnabled(MsLogging.LogLevel.Information);

        public static bool IsDebug(this MsLogging.ILogger logger) => logger.IsEnabled(MsLogging.LogLevel.Debug);

        public static bool IsTrace(this MsLogging.ILogger logger) => logger.IsEnabled(MsLogging.LogLevel.Trace);
    }
}
