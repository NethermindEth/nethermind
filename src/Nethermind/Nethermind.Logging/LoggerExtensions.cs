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

namespace Nethermind.Logging
{
    public static class LoggerExtensions
    {
        public static void Error(this ILogger logger, bool overrideCondition, LogLevel originalLevel, string message)
        {
            if (overrideCondition)
            {
                if(logger.IsError) logger.Error($"DIAG from {originalLevel}: " + message);
            }
            else
            {
                if (logger.Is(originalLevel))
                {
                    logger.Log(originalLevel, message);
                }
            }
        }
        
        public static void Warn(this ILogger logger, bool overrideCondition, LogLevel originalLevel, string message)
        {
            if (overrideCondition)
            {
                if(logger.IsWarn) logger.Warn($"DIAG from {originalLevel}: " + message);
            }
            else
            {
                if (logger.Is(originalLevel))
                {
                    logger.Log(originalLevel, message);
                }
            }
        }
        
        private static bool Is(this ILogger logger, LogLevel level)
        {
            return level switch
            {
                LogLevel.Error => logger.IsError,
                LogLevel.Warn => logger.IsWarn,
                LogLevel.Info => logger.IsInfo,
                LogLevel.Debug => logger.IsDebug,
                LogLevel.Trace => logger.IsTrace,
                _ => throw new NotSupportedException()
            };
        }

        private static void Log(this ILogger logger, LogLevel level, string message)
        {
            switch (level)
            {
                case LogLevel.Error:
                    logger.Error(message);
                    break;
                case LogLevel.Warn:
                    logger.Warn(message);
                    break;
                case LogLevel.Info:
                    logger.Info(message);
                    break;
                case LogLevel.Debug:
                    logger.Debug(message);
                    break;
                case LogLevel.Trace:
                    logger.Trace(message);
                    break;
                default:
                    throw new NotSupportedException();
            }
        }
    }
}