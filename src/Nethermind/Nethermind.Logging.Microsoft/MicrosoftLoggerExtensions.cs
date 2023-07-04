// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.Extensions.Logging;

namespace Nethermind.Logging.Microsoft
{
    public static class MicrosoftLoggerExtensions
    {
        public static bool IsError(this ILogger logger)
        {
            return logger.IsEnabled(LogLevel.Error);
        }

        public static bool IsWarn(this ILogger logger)
        {
            return logger.IsEnabled(LogLevel.Warning);
        }

        public static bool IsInfo(this ILogger logger)
        {
            return logger.IsEnabled(LogLevel.Information);
        }

        public static bool IsDebug(this ILogger logger)
        {
            return logger.IsEnabled(LogLevel.Debug);
        }

        public static bool IsTrace(this ILogger logger)
        {
            return logger.IsEnabled(LogLevel.Trace);
        }
    }
}
