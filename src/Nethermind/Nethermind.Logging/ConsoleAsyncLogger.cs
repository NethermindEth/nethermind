// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace Nethermind.Logging
{
    /// <summary>
    /// Use this class in tests only (for quick setup so there is no need to introduce NLog or other dependencies)
    /// </summary>
    public class ConsoleAsyncLogger : InterfaceLogger
    {
        private readonly LogLevel _logLevel;
        private readonly string _prefix;
        private readonly BlockingCollection<string> _queuedEntries = new BlockingCollection<string>(new ConcurrentQueue<string>());

        public void Flush()
        {
            _queuedEntries.CompleteAdding();
            _task.Wait();
        }

        private readonly Task _task;

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
                            Console.Error.WriteLine(logEntry);

                            if (_queuedEntries.IsAddingCompleted)
                            {
                                break;
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        Console.Error.WriteLine(e);
                        throw;
                    }
                },
                TaskCreationOptions.LongRunning);
        }

        private void Log(string text)
        {
            _queuedEntries.Add($"{DateTime.Now:HH:mm:ss.fff} [{Environment.CurrentManagedThreadId}] {_prefix}{text}");
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
            Log(ex is not null ? $"{text}, Exception: {ex}" : text);
        }

        public bool IsInfo => (int)_logLevel >= 2;
        public bool IsWarn => (int)_logLevel >= 1;
        public bool IsDebug => (int)_logLevel >= 3;
        public bool IsTrace => (int)_logLevel >= 4;
        public bool IsError => true;
    }
}
