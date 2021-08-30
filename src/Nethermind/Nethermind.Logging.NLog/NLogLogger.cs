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
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using NLog;
using NLog.Config;
using NLog.Fluent;
using NLog.Targets;

[assembly: InternalsVisibleTo("Nethermind.Logging.NLog.Test")]

namespace Nethermind.Logging.NLog
{
    public class NLogLogger : ILogger
    {
        private const string DefaultFileTargetName = "file-async_wrapped";

        public bool IsError { get; private set; }
        public bool IsWarn { get; private set; }
        public bool IsInfo { get; private set; }
        public bool IsDebug { get; private set; }
        public bool IsTrace { get; private set; }

        internal readonly Logger Logger;

        public NLogLogger(Type type, string fileName, string logDirectory = null, string loggerName = null, string loggerConfig = null)
        {
            loggerName = string.IsNullOrEmpty(loggerName) ? type.FullName.Replace("Nethermind.", string.Empty) : loggerName;
            Logger = LogManager.GetLogger(loggerName);
            Init(fileName, logDirectory, loggerConfig);
        }

        private void Init(string fileName, string logDirectory, string loggerConfig = null)
        {
            var logsDir = (string.IsNullOrEmpty(logDirectory) ? "logs" : logDirectory).GetApplicationResourcePath();
            if (!Directory.Exists(logsDir))
            {
                Directory.CreateDirectory(logsDir);
            }
            
            if (LogManager.Configuration?.AllTargets != null)
            {
                foreach (FileTarget target in LogManager.Configuration?.AllTargets.OfType<FileTarget>())
                {
                    string fileNameToUse = (target.Name == DefaultFileTargetName) ? fileName : target.FileName.Render(LogEventInfo.CreateNullEvent());
                    target.FileName = !Path.IsPathFullyQualified(fileNameToUse) ? Path.GetFullPath(Path.Combine(logsDir, fileNameToUse)) : fileNameToUse;
                }
            }
            
            //Add rules here for e.g. 'JsonRpc.*: Warn; Block.*: Error',
            if (loggerConfig != null)
            {
                string[] rules = loggerConfig.Split(";", StringSplitOptions.RemoveEmptyEntries);
                foreach (string rule in rules)
                {
                    string[] ruleBreakdown = rule.Split(": ");
                    string loggerNamePattern = ruleBreakdown[0].Trim();
                    string level = ruleBreakdown[1].Trim();
                    global::NLog.LogLevel logLevel = getLogLevel(level);
                    Target target = LogManager.Configuration!.FindTargetByName("auto-colored-console-async");
                    LogManager.Configuration.AddRuleForOneLevel(logLevel, target, loggerNamePattern);
                }
            }

            global::NLog.LogLevel getLogLevel(string level)
            {
                switch (level)
                {
                    case "Trace":
                        return global::NLog.LogLevel.Trace;
                    case "Debug":
                        return global::NLog.LogLevel.Debug;
                    case "Info":
                        return global::NLog.LogLevel.Info;
                    case "Warn":
                        return global::NLog.LogLevel.Warn;
                    case "Error":
                        return global::NLog.LogLevel.Error;
                    case "Fatal":
                        return global::NLog.LogLevel.Fatal;
                    default:
                        throw new ArgumentException(
                            "Configuration string was not formatted properly or LogLevel was not spelled correctly.");
                }
            }
            

            /* NOTE: minor perf gain - not planning to switch logging levels while app is running */
            // TODO: review the behaviour on log levels switching
            IsInfo = Logger.IsInfoEnabled;
            IsWarn = Logger.IsWarnEnabled;
            IsDebug = Logger.IsDebugEnabled;
            IsTrace = Logger.IsTraceEnabled;
            IsError = Logger.IsErrorEnabled || Logger.IsFatalEnabled;
        }

        public NLogLogger(string fileName, string logDirectory = null, string loggerName = null, string loggerConfig = null)
        {
            loggerName = string.IsNullOrEmpty(loggerName) ? StackTraceUsageUtils.GetClassFullName().Replace("Nethermind.", string.Empty) : loggerName;
            Logger = LogManager.GetLogger(loggerName);
            Init(fileName, logDirectory, loggerConfig);
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

        public static void Shutdown()
        {
            LogManager.Shutdown();
        }
    }
}
