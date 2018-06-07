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

namespace Nethermind.Core
{
    public class NLogLogger : ILogger
    {
        private readonly string _loggerName;
        public bool IsErrorEnabled { get; }
        public bool IsWarnEnabled { get; }
        public bool IsInfoEnabled { get; }
        public bool IsDebugEnabled { get; }
        public bool IsTraceEnabled { get; }

        private NLog.Logger _logger;

        public void ChangeLogger(string newLoggerName)
        {
            _logger = newLoggerName == null ? NLog.LogManager.GetLogger("default") : NLog.LogManager.GetLogger(newLoggerName);
        }

        public NLogLogger(string fileName, string loggerName = null)
        {
            _loggerName = loggerName;
            if (!Directory.Exists("logs"))
            {
                Directory.CreateDirectory("logs");
            }

            string[] files = Directory.GetFiles("logs");
            foreach (string file in files)
            {
                // TODO: temp for testing
                try
                {
                    File.Delete(file);
                }
                catch (Exception e)
                {
                    // ignore
                }
            }

            ChangeLogger(loggerName);
            if (NLog.LogManager.Configuration.AllTargets.SingleOrDefault(t => t.Name == "file") is FileTarget target)
            {
                target.FileName = Path.Combine("logs", fileName);
            }

            /* NOTE: minor perf gain - not planning to switch logging levels while app is running */
            IsInfoEnabled = _logger.IsInfoEnabled;
            IsWarnEnabled = _logger.IsWarnEnabled;
            IsDebugEnabled = _logger.IsDebugEnabled;
            IsTraceEnabled = _logger.IsTraceEnabled;
            IsErrorEnabled = _logger.IsErrorEnabled || _logger.IsFatalEnabled;
        }

        public void LogLoggerInfo()
        {
            Trace($"{_loggerName} TRACE enabled");
            Debug($"{_loggerName} DEBUG enabled");
            Info($"{_loggerName} INFO enabled");
            Warn($"{_loggerName} WARN enabled");
            Error($"{_loggerName} ERROR enabled");
        }

        public void Log(string text)
        {
            _logger.Info(text);
        }

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