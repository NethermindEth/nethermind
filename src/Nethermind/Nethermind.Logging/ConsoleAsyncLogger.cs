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
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Logging
{
    /// <summary>
    /// Use this class in tests only (for quick setup so there is no need to introduce NLog or other dependencies)
    /// </summary>
    public class ConsoleAsyncLogger : ILogger
    {
        private readonly LogLevel _logLevel;
        private readonly string _prefix;
        private readonly BlockingCollection<string> _queuedEntries = new BlockingCollection<string>(new ConcurrentQueue<string>());

        public void Flush()
        {
            _queuedEntries.CompleteAdding();
            _task.Wait();
        }

        private Task _task;
        
        public ConsoleAsyncLogger(LogLevel logLevel, string prefix = null)
        {
            _logLevel = logLevel;
            _prefix = prefix;
            _task = Task.Factory.StartNew(() =>
                {
                    try
                    {
                        foreach (string logEntry in _queuedEntries.GetConsumingEnumerable())
                        {
                            Console.WriteLine(logEntry);

                            if (_queuedEntries.IsAddingCompleted)
                            {
                                break;
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e);
                        throw;
                    }
                },
                TaskCreationOptions.LongRunning);
        }

        private void Log(string text)
        {
            _queuedEntries.Add($"{DateTime.Now:HH:mm:ss.fff} [{Thread.CurrentThread.ManagedThreadId}] {_prefix}{text}");
        }

        public void Info(string text)
        {
            Log(text);
        }

        public void Warn(string text)
        {
            Log(text);
        }

        public void Debug(string text)
        {
            Log(text);
        }

        public void Trace(string text)
        {
            Log(text);
        }

        public void Error(string text, Exception ex = null)
        {
            Log(ex != null ? $"{text}, Exception: {ex}" : text);
        }

        public bool IsInfo => (int) _logLevel >= 2;
        public bool IsWarn => (int) _logLevel >= 1;
        public bool IsDebug => (int) _logLevel >= 3;
        public bool IsTrace => (int) _logLevel >= 4;
        public bool IsError => true;
    }
}
