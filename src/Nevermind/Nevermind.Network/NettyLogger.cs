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
using DotNetty.Common.Internal.Logging;

namespace Nevermind.Network
{
    public class NettyLogger : IInternalLogger
    {
        public void Trace(string msg)
        {
            throw new NotImplementedException();
        }

        public void Trace(string format, object arg)
        {
            throw new NotImplementedException();
        }

        public void Trace(string format, object argA, object argB)
        {
            throw new NotImplementedException();
        }

        public void Trace(string format, params object[] arguments)
        {
            throw new NotImplementedException();
        }

        public void Trace(string msg, Exception t)
        {
            throw new NotImplementedException();
        }

        public void Trace(Exception t)
        {
            throw new NotImplementedException();
        }

        public void Debug(string msg)
        {
            throw new NotImplementedException();
        }

        public void Debug(string format, object arg)
        {
            throw new NotImplementedException();
        }

        public void Debug(string format, object argA, object argB)
        {
            throw new NotImplementedException();
        }

        public void Debug(string format, params object[] arguments)
        {
            throw new NotImplementedException();
        }

        public void Debug(string msg, Exception t)
        {
            throw new NotImplementedException();
        }

        public void Debug(Exception t)
        {
            throw new NotImplementedException();
        }

        public void Info(string msg)
        {
            throw new NotImplementedException();
        }

        public void Info(string format, object arg)
        {
            throw new NotImplementedException();
        }

        public void Info(string format, object argA, object argB)
        {
            throw new NotImplementedException();
        }

        public void Info(string format, params object[] arguments)
        {
            throw new NotImplementedException();
        }

        public void Info(string msg, Exception t)
        {
            throw new NotImplementedException();
        }

        public void Info(Exception t)
        {
            throw new NotImplementedException();
        }

        public void Warn(string msg)
        {
            throw new NotImplementedException();
        }

        public void Warn(string format, object arg)
        {
            throw new NotImplementedException();
        }

        public void Warn(string format, params object[] arguments)
        {
            throw new NotImplementedException();
        }

        public void Warn(string format, object argA, object argB)
        {
            throw new NotImplementedException();
        }

        public void Warn(string msg, Exception t)
        {
            throw new NotImplementedException();
        }

        public void Warn(Exception t)
        {
            throw new NotImplementedException();
        }

        public void Error(string msg)
        {
            throw new NotImplementedException();
        }

        public void Error(string format, object arg)
        {
            throw new NotImplementedException();
        }

        public void Error(string format, object argA, object argB)
        {
            throw new NotImplementedException();
        }

        public void Error(string format, params object[] arguments)
        {
            throw new NotImplementedException();
        }

        public void Error(string msg, Exception t)
        {
            throw new NotImplementedException();
        }

        public void Error(Exception t)
        {
            throw new NotImplementedException();
        }

        public bool IsEnabled(InternalLogLevel level)
        {
            throw new NotImplementedException();
        }

        public void Log(InternalLogLevel level, string msg)
        {
            throw new NotImplementedException();
        }

        public void Log(InternalLogLevel level, string format, object arg)
        {
            throw new NotImplementedException();
        }

        public void Log(InternalLogLevel level, string format, object argA, object argB)
        {
            throw new NotImplementedException();
        }

        public void Log(InternalLogLevel level, string format, params object[] arguments)
        {
            throw new NotImplementedException();
        }

        public void Log(InternalLogLevel level, string msg, Exception t)
        {
            throw new NotImplementedException();
        }

        public void Log(InternalLogLevel level, Exception t)
        {
            throw new NotImplementedException();
        }

        public string Name { get; }
        public bool TraceEnabled { get; }
        public bool DebugEnabled { get; }
        public bool InfoEnabled { get; }
        public bool WarnEnabled { get; }
        public bool ErrorEnabled { get; }
    }
}