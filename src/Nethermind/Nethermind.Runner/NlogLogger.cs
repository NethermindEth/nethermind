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
using System.Linq;
using NLog.Targets;
using ILogger = Nethermind.Core.ILogger;

namespace Nethermind.Runner
{
    public class NLogLogger : ILogger
    {
        private readonly NLog.Logger _logger;

        public NLogLogger(string fileName, string loggerName = null)
        {
            _logger = loggerName == null ? NLog.LogManager.GetLogger("default") : NLog.LogManager.GetLogger(loggerName);
            if (NLog.LogManager.Configuration.AllTargets.SingleOrDefault(t => t.Name == "file") is FileTarget target)
            {
                target.FileName = fileName;
            }
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

        public bool IsInfoEnabled => _logger.IsInfoEnabled;
        public bool IsWarnEnabled => _logger.IsWarnEnabled;
        public bool IsDebugEnabled => _logger.IsDebugEnabled;
        public bool IsTraceEnabled => _logger.IsTraceEnabled;
        public bool IsErrorEnabled => _logger.IsErrorEnabled || _logger.IsFatalEnabled;
    }
}