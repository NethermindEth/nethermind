// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using MsILogger = global::Microsoft.Extensions.Logging.ILogger;
using MsLogLevel = global::Microsoft.Extensions.Logging.LogLevel;

namespace Nethermind.Logging.Microsoft
{
    public static class MicrosoftLoggerExtensions
    {
        public static bool IsError(this MsILogger logger) => logger.IsEnabled(MsLogLevel.Error);

        public static bool IsWarn(this MsILogger logger) => logger.IsEnabled(MsLogLevel.Warning);

        public static bool IsInfo(this MsILogger logger) => logger.IsEnabled(MsLogLevel.Information);

        public static bool IsDebug(this MsILogger logger) => logger.IsEnabled(MsLogLevel.Debug);

        public static bool IsTrace(this MsILogger logger) => logger.IsEnabled(MsLogLevel.Trace);
    }
}
