// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
