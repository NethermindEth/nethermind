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

namespace Nethermind.Logging
{
    /// <summary>
    /// Just use before the logger is configured
    /// </summary>
    public class SimpleConsoleLogger : ILogger
    {
        private SimpleConsoleLogger()
        {
        }

        public static SimpleConsoleLogger Instance { get; } = new SimpleConsoleLogger();

        public void Info(string text)
        {
            WriteEntry(text);
        }

        public void Warn(string text)
        {
            WriteEntry(text);
        }

        public void Debug(string text)
        {
            WriteEntry(text);
        }

        public void Trace(string text)
        {
            WriteEntry(text);
        }

        public void Error(string text, Exception ex = null)
        {
            WriteEntry(text + " " + ex);
        }

        private void WriteEntry(string text)
        {
            Console.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss.ffff|") + text);
        }

        public bool IsInfo => true;
        public bool IsWarn => true;
        public bool IsDebug => true;
        public bool IsTrace => true;
        public bool IsError => true;
    }
}
