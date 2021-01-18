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

using System;
using System.Collections.Generic;
using Nethermind.Logging;

namespace Nethermind.Core.Test
{
    public class TestLogger : ILogger
    {
        public List<string> LogList { get; set; } = new List<string>();

        public void Info(string text)
        {
            LogList.Add(text);
        }

        public void Warn(string text)
        {
            LogList.Add(text);
        }

        public void Debug(string text)
        {
            LogList.Add(text);
        }

        public void Trace(string text)
        {
            LogList.Add(text);
        }

        public void Error(string text, Exception ex = null)
        {
            LogList.Add(text);
        }

        public bool IsInfo { get; set; } = true;
        public bool IsWarn { get; set; } = true;
        public bool IsDebug { get; set; } = true;
        public bool IsTrace { get; set; } = true;
        public bool IsError { get; set; } = true;
    }
}
