/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.IO;
using System.Linq;
using NLog.Targets;

namespace Nethermind.Logging
{
    public class NLogLogger : ILogger
    {
        public bool IsError { get; }
        public bool IsWarn { get; }
        public bool IsInfo { get; }
        public bool IsDebug { get; }
        public bool IsTrace { get; }

        internal readonly NLog.Logger Logger;

        public NLogLogger(Type type, string fileName, string logDirectory = null, string loggerName = null)
        {
            loggerName = string.IsNullOrEmpty(loggerName) ? type.FullName.Replace("Nethermind.", string.Empty) : loggerName;
            Logger = NLog.LogManager.GetLogger(loggerName);

            var logsDir = string.IsNullOrEmpty(logDirectory) ? "logs".GetApplicationResourcePath() : logDirectory;
            if (!Directory.Exists(logsDir))
            {
                Directory.CreateDirectory(logsDir);
            }

            if (NLog.LogManager.Configuration?.AllTargets.SingleOrDefault(t => t.Name == "file") is FileTarget target)
            {
                target.FileName = !Path.IsPathFullyQualified(fileName) ? Path.Combine(logsDir, fileName) : fileName;
            }

            /* NOTE: minor perf gain - not planning to switch logging levels while app is running */
            // TODO: review the behaviour on log levels switching which we have just added recently...
            IsInfo = Logger.IsInfoEnabled;
            IsWarn = Logger.IsWarnEnabled;
            IsDebug = Logger.IsDebugEnabled;
            IsTrace = Logger.IsTraceEnabled;
            IsError = Logger.IsErrorEnabled || Logger.IsFatalEnabled;
        }

        public NLogLogger(string fileName, string logDirectory = null, string loggerName = null)
        {
            loggerName = string.IsNullOrEmpty(loggerName) ? StackTraceUsageUtils.GetClassFullName().Replace("Nethermind.", string.Empty) : loggerName;
            Logger = NLog.LogManager.GetLogger(loggerName);

            var logsDir = string.IsNullOrEmpty(logDirectory) ? "logs".GetApplicationResourcePath(): logDirectory;
            if (!Directory.Exists(logsDir))
            {
                Directory.CreateDirectory(logsDir);
            }

            if (NLog.LogManager.Configuration?.AllTargets.SingleOrDefault(t => t.Name == "file") is FileTarget target)
            {
                target.FileName = !Path.IsPathFullyQualified(fileName) ? Path.Combine(logsDir, fileName) : fileName;
            }

            /* NOTE: minor perf gain - not planning to switch logging levels while app is running */
            IsInfo = Logger.IsInfoEnabled;
            IsWarn = Logger.IsWarnEnabled;
            IsDebug = Logger.IsDebugEnabled;
            IsTrace = Logger.IsTraceEnabled;
            IsError = Logger.IsErrorEnabled || Logger.IsFatalEnabled;
        }

        private string Level
        {
            get
            {
                if (IsTrace) return "Trace";

                if (IsDebug) return "Debug";

                if (IsInfo) return "Info";

                if (IsWarn) return "Warn";

                if (IsError) return "Error";

                return "None";
            }
        }

        private void Log(string text)
        {
            Logger.Info(text);
        }

        public void Info(string text)
        {
            Logger.Info(text);
        }

        public void Warn(string text)
        {
            Logger.Warn(text);
        }

        public void Debug(string text)
        {
            Logger.Debug(text);
        }

        public void Trace(string text)
        {
            Logger.Trace(text);
        }

        public void Error(string text, Exception ex = null)
        {
            Logger.Error(ex, text);
        }
    }
}