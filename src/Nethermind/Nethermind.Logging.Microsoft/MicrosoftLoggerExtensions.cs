//  Copyright (c) 2021 Demerzel Solutions Limited
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
