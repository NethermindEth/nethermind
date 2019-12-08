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

using Microsoft.Extensions.Logging;

namespace Nethermind.Logging
{
    public static class MicrosoftLoggerExtensions
    {
        public static bool IsError(this Microsoft.Extensions.Logging.ILogger logger)
        {
            return logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Error);
        }
        
        public static bool IsWarn(this Microsoft.Extensions.Logging.ILogger logger)
        {
            return logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Warning);
        }
        
        public static bool IsInfo(this Microsoft.Extensions.Logging.ILogger logger)
        {
            return logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Information);
        }
        
        public static bool IsDebug(this Microsoft.Extensions.Logging.ILogger logger)
        {
            return logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug);
        }
        
        public static bool IsTrace(this Microsoft.Extensions.Logging.ILogger logger)
        {
            return logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Trace);
        }
        
        public static void Info(
            this Microsoft.Extensions.Logging.ILogger logger,
            EventId eventId,
            string message,
            params object[] args)
        {
            logger.LogInformation(eventId, message, args);
        }
        
        public static void Debug(
            this Microsoft.Extensions.Logging.ILogger logger,
            EventId eventId,
            string message,
            params object[] args)
        {
            logger.LogDebug(eventId, message, args);
        }
    }
}