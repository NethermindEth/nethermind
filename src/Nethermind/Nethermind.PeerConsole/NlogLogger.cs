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
using Nethermind.Core;

namespace Nethermind.PeerConsole
{
    public class NLogLogger : ILogger
    {
        private readonly NLog.Logger _logger;

        public NLogLogger(string name = null)
        {
            _logger = name == null ? NLog.LogManager.GetLogger("default") : NLog.LogManager.GetLogger(name);
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

        public bool IsInfo => _logger.IsInfoEnabled;
        public bool IsWarn => _logger.IsWarnEnabled;
        public bool IsDebug => _logger.IsDebugEnabled;
        public bool IsTrace => _logger.IsTraceEnabled;
        public bool IsError => _logger.IsErrorEnabled || _logger.IsFatalEnabled;
    }
}