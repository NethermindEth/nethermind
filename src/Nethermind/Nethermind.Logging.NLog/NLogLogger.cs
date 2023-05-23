// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using NLog;
using NLog.Targets;

namespace Nethermind.Logging.NLog
{
    public class NLogLogger : ILogger
    {
        public bool IsError { get; }
        public bool IsWarn { get; }
        public bool IsInfo { get; }
        public bool IsDebug { get; }
        public bool IsTrace { get; }

        public string Name { get; }

        private readonly Logger _logger;

        public NLogLogger(Type type) : this(GetTypeName(type.FullName))
        {
        }

        public NLogLogger(string loggerName = null)
        {
            loggerName = string.IsNullOrEmpty(loggerName) ? GetTypeName(StackTraceUsageUtils.GetClassFullName()) : loggerName;
            _logger = LogManager.GetLogger(loggerName);

            /* NOTE: minor perf gain - not planning to switch logging levels while app is running */
            // TODO: review the behaviour on log levels switching
            IsInfo = _logger.IsInfoEnabled;
            IsWarn = _logger.IsWarnEnabled;
            IsDebug = _logger.IsDebugEnabled;
            IsTrace = _logger.IsTraceEnabled;
            IsError = _logger.IsErrorEnabled || _logger.IsFatalEnabled;
            Name = _logger.Name;
        }

        private static string GetTypeName(string typeName) => typeName.Replace("Nethermind.", string.Empty);

        public void Info(string text)
        {
            _logger.Info(text);
        }

        public void Warn(string text)
        {
            _logger.Warn(text);
        }

        public void Debug(string text)
        {
            _logger.Debug(text);
        }

        public void Trace(string text)
        {
            _logger.Trace(text);
        }

        public void Error(string text, Exception ex = null)
        {
            _logger.Error(ex, text);
        }
    }
}
