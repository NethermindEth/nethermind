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

namespace Nethermind.Core.Logging
{
    public class NLogLogger : ILogger
    {
        public bool IsErrorEnabled { get; }
        public bool IsWarnEnabled { get; }
        public bool IsInfoEnabled { get; }
        public bool IsDebugEnabled { get; }
        public bool IsTraceEnabled { get; }

        // ReSharper disable once InconsistentNaming
        internal NLog.Logger _logger;

        public NLogLogger(string fileName)
        {
            _logger = NLog.LogManager.GetLogger(StackTraceUsageUtils.GetClassFullName());
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