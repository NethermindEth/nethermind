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
using System.IO;
using System.Linq;
using NLog.Targets;

namespace Nethermind.Core.Logging
{
    public class NLogLogger : ILogger
    {
        public bool IsErrorEnabled { get; }
        public bool IsWarnEnabled { get; }
        public bool IsInfoEnabled { get; }
        public bool IsDebugEnabled { get; }
        public bool IsTraceEnabled { get; }
        public bool IsNoteEnabled { get; }

        internal readonly NLog.Logger Logger;
        private readonly NLog.Logger _noteLogger;

        public NLogLogger(string fileName)
        {
            Logger = NLog.LogManager.GetLogger(StackTraceUsageUtils.GetClassFullName()
                .Replace("Nethermind.", string.Empty));
            if (!Directory.Exists("logs"))
            {
                Directory.CreateDirectory("logs");
            }

            if (NLog.LogManager.Configuration.AllTargets.SingleOrDefault(t => t.Name == "file") is FileTarget target)
            {
                target.FileName = !Path.IsPathFullyQualified(fileName) ? Path.Combine("logs", fileName) : fileName;
            }

            /* NOTE: minor perf gain - not planning to switch logging levels while app is running */
            IsInfoEnabled = Logger.IsInfoEnabled;
            IsWarnEnabled = Logger.IsWarnEnabled;
            IsDebugEnabled = Logger.IsDebugEnabled;
            IsTraceEnabled = Logger.IsTraceEnabled;
            IsErrorEnabled = Logger.IsErrorEnabled || Logger.IsFatalEnabled;

            Log($"Configured {Logger.Name} logger at level {Level}");

            _noteLogger = NLog.LogManager.GetLogger("NoteLogger");
            IsNoteEnabled = _noteLogger.IsInfoEnabled;
        }

        private string Level
        {
            get
            {
                if (IsTraceEnabled)
                {
                    return "Trace";
                }

                if (IsDebugEnabled)
                {
                    return "Debug";
                }

                if (IsInfoEnabled)
                {
                    return "Info";
                }

                if (IsWarnEnabled)
                {
                    return "Warn";
                }

                if (IsErrorEnabled)
                {
                    return "Error";
                }

                return "None";
            }
        }

        private void Log(string text)
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

        public void Note(string text)
        {
            _noteLogger.Info(text);
        }
    }
}