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
// 

using System;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Core.Test
{
    public class NUnitLogger : ILogger
    {
        private readonly LogLevel _level;

        public NUnitLogger(LogLevel level)
        {
            _level = level;
        }

        public void Info(string text)
        {
            if (IsInfo)
            {
                Log(text);
            }
        }

        public void Warn(string text)
        {
            if (IsWarn)
            {
                Log(text);
            }
        }

        public void Debug(string text)
        {
            if (IsDebug)
            {
                Log(text);
            }
        }

        public void Trace(string text)
        {
            if (IsTrace)
            {
                Log(text);
            }
        }

        public void Error(string text, Exception ex = null)
        {
            if (IsError)
            {
                Log(text, ex);
            }
        }

        public bool IsInfo => CheckLevel(LogLevel.Info);
        public bool IsWarn => CheckLevel(LogLevel.Warn);
        public bool IsDebug => CheckLevel(LogLevel.Debug);
        public bool IsTrace => CheckLevel(LogLevel.Trace);
        public bool IsError => CheckLevel(LogLevel.Error);

        private bool CheckLevel(LogLevel logLevel) => _level >= logLevel;

        private static void Log(string text, Exception? ex = null)
        {
            Console.WriteLine(text);
            // TestContext.Out.WriteLine(text);

            if (ex != null)
            {
                TestContext.Out.WriteLine(ex.ToString());
            }
        }
    }
}
