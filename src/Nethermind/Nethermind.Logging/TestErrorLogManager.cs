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
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Nethermind.Logging;

public class TestErrorLogManager : ILogManager
{
    private readonly ConcurrentQueue<Error> _errors = new();
        
    public IReadOnlyCollection<Error> Errors => _errors;

    public ILogger GetClassLogger(Type type) => GetClassLogger();

    public ILogger GetClassLogger<T>() => GetClassLogger();

    public ILogger GetClassLogger() => new TestErrorLogger(_errors);

    public ILogger GetLogger(string loggerName) => GetClassLogger();

    public class TestErrorLogger : ILogger
    {
        private readonly ConcurrentQueue<Error> _errors;

        public TestErrorLogger(ConcurrentQueue<Error> errors)
        {
            _errors = errors;
        }

        public void Info(string text) { }
        public void Warn(string text) { }
        public void Debug(string text) { }
        public void Trace(string text) { }
        public void Error(string text, Exception ex = null)
        {
            _errors.Enqueue(new Error() { Text = text, Exception = ex });
        }
        public bool IsInfo => false;
        public bool IsWarn => false;
        public bool IsDebug => true;
        public bool IsTrace => false;
        public bool IsError => true;
    }

    public record Error
    {
        public string Text { get; init; }
        public Exception Exception { get; init; }
    }
}
