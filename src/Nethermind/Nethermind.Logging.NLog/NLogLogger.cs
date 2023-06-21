// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Threading;

using NLog;

#nullable enable

namespace Nethermind.Logging.NLog
{
    public class NLogLogger : ILogger
    {
        private readonly static LogQueue _logQueue = new();

        public bool IsError { get; }
        public bool IsWarn { get; }
        public bool IsInfo { get; }
        public bool IsDebug { get; }
        public bool IsTrace { get; }

        public string Name { get; }

        private readonly Logger _logger;

        public NLogLogger(Type type) : this(GetTypeName(type.FullName!))
        {
        }

        public NLogLogger(string? loggerName = null)
        {
            loggerName = string.IsNullOrEmpty(loggerName) ? GetTypeName(StackTraceUsageUtils.GetClassFullName()) : loggerName;
            _logger = LogManager.GetLogger(loggerName);

            /* NOTE: minor perf gain - not planning to switch logging levels while app is running */
            // TODO: review the behaviour on log levels switching
            IsInfo = _logger.IsInfoEnabled;
            IsWarn = _logger.IsWarnEnabled;
            IsDebug = _logger.IsDebugEnabled;
            IsTrace = _logger.IsTraceEnabled;
            IsError = _logger.IsErrorEnabled || _logger.IsFatalEnabled;
            Name = _logger.Name;
        }

        private static string GetTypeName(string typeName) => typeName.Replace("Nethermind.", string.Empty);

        public void Info(string text)
        {
            if (IsInfo)
                _logQueue.Log(_logger, LogLevel.Info, text);
        }

        public void Warn(string text)
        {
            if (IsWarn)
                _logQueue.Log(_logger, LogLevel.Warn, text);
        }

        public void Debug(string text)
        {
            if (IsDebug)
                _logQueue.Log(_logger, LogLevel.Debug, text);
        }

        public void Trace(string text)
        {
            if (IsTrace)
                _logQueue.Log(_logger, LogLevel.Trace, text);
        }

        public void Error(string text, Exception? ex = null)
        {
            if (IsError)
                _logQueue.Log(_logger, LogLevel.Error, text, ex);
        }

        private class LogQueue : IThreadPoolWorkItem
        {
            private readonly ConcurrentQueue<LogEntry> _workItems = new();
            private int _doingWork;

            public void Log(Logger logger, LogLevel level, string text, Exception? ex = null)
            {
                _workItems.Enqueue(new(logger, level, text, ex));

                // Set working if it wasn't (via atomic Interlocked).
                if (Interlocked.CompareExchange(ref _doingWork, 1, 0) == 0)
                {
                    // Wasn't working, schedule.
                    ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
                }
            }

            void IThreadPoolWorkItem.Execute()
            {
                while (true)
                {
                    while (_workItems.TryDequeue(out LogEntry item))
                    {
                        switch (item.Level)
                        {
                            case LogLevel.Error:
                                item.Logger.Error(item.Exception, item.Message);
                                break;
                            case LogLevel.Warn:
                                item.Logger.Warn(item.Message);
                                break;
                            case LogLevel.Info:
                                item.Logger.Info(item.Message);
                                break;
                            case LogLevel.Debug:
                                item.Logger.Debug(item.Message);
                                break;
                            case LogLevel.Trace:
                                item.Logger.Trace(item.Message);
                                break;
                        }
                    }

                    // All work done.

                    // Set _doingWork (0 == false) prior to checking IsEmpty to catch any missed work in interim.
                    // This doesn't need to be volatile due to the following barrier (i.e. it is volatile).
                    _doingWork = 0;

                    // Ensure _doingWork is written before IsEmpty is read.
                    // As they are two different memory locations, we insert a barrier to guarantee ordering.
                    Thread.MemoryBarrier();

                    // Check if there is work to do
                    if (_workItems.IsEmpty)
                    {
                        // Nothing to do, exit.
                        break;
                    }

                    // Is work, can we set it as active again (via atomic Interlocked), prior to scheduling?
                    if (Interlocked.Exchange(ref _doingWork, 1) == 1)
                    {
                        // Execute has been rescheduled already, exit.
                        break;
                    }

                    // Is work, wasn't already scheduled so continue loop.
                }
            }
        }

        private readonly struct LogEntry
        {
            public readonly Logger Logger;
            public readonly LogLevel Level;
            public readonly string Message;
            public readonly Exception? Exception;

            public LogEntry(Logger logger, LogLevel level, string message, Exception? exception)
            {
                Logger = logger;
                Level = level;
                Message = message;
                Exception = exception;
            }
        }
    }
}
