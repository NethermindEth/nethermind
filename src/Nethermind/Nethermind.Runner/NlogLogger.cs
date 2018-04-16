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
using NLog;
using ILogger = Nethermind.Core.ILogger;

namespace Nethermind.Runner
{
    public class NLogLogger : ILogger
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public void Log(string text)
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

        public bool IsInfo => Logger.IsInfoEnabled;
        public bool IsWarn => Logger.IsWarnEnabled;
        public bool IsDebug => Logger.IsDebugEnabled;
        public bool IsTrace => Logger.IsTraceEnabled;
        public bool IsError => Logger.IsErrorEnabled || Logger.IsFatalEnabled;
    }
}