// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NLog;
using NLog.Config;
using NLog.Targets;
using Level = NLog.LogLevel;

namespace Nethermind.Logging.NLog
{
    public class NLogManager : ILogManager, IDisposable
    {
        private const string DefaultFileTargetName = "file-async_wrapped";
        private const string DefaultFolder = "logs";

        public NLogManager(string logFileName, string logDirectory = null, string logRules = null)
        {
            Setup(logFileName, logDirectory, logRules);
            // Required since 'NLog.config' could change during runtime, we need to re-apply the configuration
            _logManagerOnConfigurationChanged = (sender, args) => Setup(logFileName, logDirectory, logRules);
            LogManager.ConfigurationChanged += _logManagerOnConfigurationChanged;
        }

        private static void Setup(string logFileName, string logDirectory = null, string logRules = null)
        {
            logDirectory = SetupLogDirectory(logDirectory);
            SetupLogFile(logFileName, logDirectory);
            SetupLogRules(logRules);
        }

        private static void SetupLogFile(string logFileName, string logDirectory)
        {
            if (LogManager.Configuration?.AllTargets is not null)
            {
                foreach (FileTarget target in LogManager.Configuration?.AllTargets.OfType<FileTarget>())
                {
                    string fileNameToUse = (target.Name == DefaultFileTargetName) ? logFileName : target.FileName.Render(LogEventInfo.CreateNullEvent());
                    target.FileName = !Path.IsPathFullyQualified(fileNameToUse) ? Path.GetFullPath(Path.Combine(logDirectory, fileNameToUse)) : fileNameToUse;
                }
            }
        }

        private static string SetupLogDirectory(string logDirectory)
        {
            logDirectory = (string.IsNullOrEmpty(logDirectory) ? DefaultFolder : logDirectory).GetApplicationResourcePath();
            if (!Directory.Exists(logDirectory))
            {
                Directory.CreateDirectory(logDirectory);
            }

            return logDirectory;
        }

        private static readonly ConcurrentDictionary<Type, ILogger> s_loggers = new();
        private static readonly Func<Type, ILogger> s_loggerBuilder = BuildLogger;
        private readonly EventHandler<LoggingConfigurationChangedEventArgs> _logManagerOnConfigurationChanged;

        private static ILogger BuildLogger(Type type) => new(new NLogLogger(type));

        public ILogger GetClassLogger(Type type) => s_loggers.GetOrAdd(type, s_loggerBuilder);

        public ILogger GetClassLogger<T>() => GetClassLogger(typeof(T));

        public ILogger GetClassLogger() => new(new NLogLogger());

        public ILogger GetLogger(string loggerName) => new(new NLogLogger(loggerName));

        public void SetGlobalVariable(string name, object value)
        {
            GlobalDiagnosticsContext.Set(name, value);
        }

        private static void SetupLogRules(string logRules)
        {
            //Add rules here for e.g. 'JsonRpc.*: Warn; Block.*: Error;',
            if (logRules is not null)
            {
                IList<LoggingRule> configurationLoggingRules = LogManager.Configuration.LoggingRules;
                lock (configurationLoggingRules)
                {
                    Target[] targets = GetTargets(configurationLoggingRules);
                    IEnumerable<LoggingRule> loggingRules = ParseRules(logRules, targets);
                    foreach (LoggingRule loggingRule in loggingRules)
                    {
                        RemoveOverridenRules(configurationLoggingRules, loggingRule);
                        configurationLoggingRules.Add(loggingRule);
                    }
                }
            }
        }

        private static Target[] GetTargets(IList<LoggingRule> configurationLoggingRules) =>
            configurationLoggingRules.SelectMany(r => r.Targets).Distinct().ToArray();

        private static void RemoveOverridenRules(IList<LoggingRule> configurationLoggingRules, LoggingRule loggingRule)
        {
            string reqexPattern = $"^{loggingRule.LoggerNamePattern.Replace(".", "\\.").Replace("*", ".*")}$";
            for (int j = 0; j < configurationLoggingRules.Count;)
            {
                if (Regex.IsMatch(configurationLoggingRules[j].LoggerNamePattern, reqexPattern))
                {
                    configurationLoggingRules.RemoveAt(j);
                }
                else
                {
                    j++;
                }
            }
        }

        private static IEnumerable<LoggingRule> ParseRules(string logRules, Target[] targets)
        {
            string[] rules = logRules.Split(";", StringSplitOptions.RemoveEmptyEntries);
            foreach (string rule in rules)
            {
                string loggerNamePattern;
                Level logLevel;
                try
                {
                    string[] ruleBreakdown = rule.Split(":");
                    if (ruleBreakdown.Length == 2)
                    {
                        loggerNamePattern = ruleBreakdown[0].Trim();
                        logLevel = Level.FromString(ruleBreakdown[1].Trim());
                    }
                    else
                    {
                        throw new ArgumentException($"Invalid rule '{rule}' in InitConfig.LogRules '{logRules}'");
                    }
                }
                catch (ArgumentException e)
                {
                    throw new ArgumentException($"Invalid rule '{rule}' in InitConfig.LogRules '{logRules}'", e);
                }

                yield return CreateLoggingRule(targets, logLevel, loggerNamePattern);
            }
        }

        private static LoggingRule CreateLoggingRule(Target[] targets, Level level, string loggerNamePattern)
        {
            LoggingRule loggingRule = new(loggerNamePattern, level, Level.Fatal, targets[0]);
            for (int i = 1; i < targets.Length; i++)
            {
                loggingRule.Targets.Add(targets[i]);
            }

            return loggingRule;
        }

        public static void Shutdown()
        {
            LogManager.Shutdown();
        }

        public void Dispose()
        {
            LogManager.ConfigurationChanged -= _logManagerOnConfigurationChanged;
        }
    }
}
