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

using System;
using System.Threading;

namespace Nethermind.Logging
{
    /// <summary>
    /// LimboLogs redirects logs to nowhere (limbo) and it should be always used in tests as it guarantees that
    /// we test any potential issues with the log message construction.
    /// Imagine that we have a construction like if(_logger.IsTrace) _logger.Trace("somethingThatIsNull.ToString()")
    /// This would not be tested until we switched the logger to Trace level and this, in turn,
    /// would slow down the tests and increase memory construction due to the log files generation.
    /// Instead we use LimboLogs that returns a logger that always causes the log message to be created and so we can
    /// detect somethingThatIsNull.ToString() throwing an error.
    /// </summary>
    public class LimboLogs : ILogManager
    {
        private LimboLogs()
        {
        }

        private static LimboLogs _instance;
        
        public static LimboLogs Instance => _instance ?? LazyInitializer.EnsureInitialized(ref _instance, () => new LimboLogs());

        public ILogger GetClassLogger(Type type)
        {
            return LimboTraceLogger.Instance;
        }

        public ILogger GetClassLogger<T>()
        {
            return LimboTraceLogger.Instance;
        }

        public ILogger GetClassLogger()
        {
            return LimboTraceLogger.Instance;
        }

        public ILogger GetLogger(string loggerName)
        {
            return LimboTraceLogger.Instance;
        }
    }
}
