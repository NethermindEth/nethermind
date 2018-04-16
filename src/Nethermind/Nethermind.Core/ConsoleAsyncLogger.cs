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
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Core
{
    public class ConsoleAsyncLogger : ILogger
    {
        private readonly BlockingCollection<string> _queuedEntries = new BlockingCollection<string>(new ConcurrentQueue<string>());

        public ConsoleAsyncLogger()
        {
            Task.Factory.StartNew(() =>
                {
                    foreach (string logEntry in _queuedEntries.GetConsumingEnumerable())
                    {
                        Console.WriteLine(logEntry);
                    }
                },
                TaskCreationOptions.LongRunning);
        }

        public void Log(string text)
        {
            _queuedEntries.Add($"{DateTime.Now:HH:mm:ss.fff} [{Thread.CurrentThread.ManagedThreadId}] {text}");
        }

        public void Debug(string text)
        {
            Log(text);
        }

        public void Error(string text, Exception ex = null)
        {
            Log(ex != null ? $"{text}, Exception: {ex}" : text);
        }
    }
}