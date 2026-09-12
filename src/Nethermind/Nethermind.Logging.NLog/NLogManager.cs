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
using NLog.Targets.Wrappers;
using Level = NLog.LogLevel;

namespace Nethermind.Logging.NLog;

public class NLogManager : ILogManager, IDisposable
{
    private const string DefaultFileTargetName = "file-async_wrapped";
    private const string DefaultFolder = "logs";
    private const string SeqTargetName = "seq";

    /// <summary>
    /// The constructor to use when the configuration is not yet initialized.
    /// </summary>
    public NLogManager() { /* Log in temp dir? */ }

    public NLogManager(string logFileName, string? logDirectory = null, string? logRules = null)
    {
        Setup(logFileName, logDirectory, logRules);
        // Required since 'NLog.config' could change during runtime, we need to re-apply the configuration
        _logManagerOnConfigurationChanged = (sender, args) => Setup(logFileName, logDirectory, logRules);
        LogManager.ConfigurationChanged += _logManagerOnConfigurationChanged;
        Static.LogManager = this;
    }

    private static void Setup(string logFileName, string? logDirectory = null, string? logRules = null)
    {
        logDirectory = SetupLogDirectory(logDirectory);
        SetupLogFile(logFileName, logDirectory);
        SetupLogRules(logRules);
        LogManager.ReconfigExistingLoggers();
    }

    private static void SetupLogFile(string logFileName, string logDirectory)
    {
        if (LogManager.Configuration?.AllTargets is { } allTargets)
        {
            foreach (FileTarget target in allTargets.OfType<FileTarget>())
            {
                string fileNameToUse = (target.Name == DefaultFileTargetName) ? logFileName : target.FileName.Render(LogEventInfo.CreateNullEvent());
                target.FileName = !Path.IsPathFullyQualified(fileNameToUse) ? Path.GetFullPath(Path.Combine(logDirectory, fileNameToUse)) : fileNameToUse;
            }
        }
    }

    private static string SetupLogDirectory(string? logDirectory)
    {
        logDirectory = (string.IsNullOrEmpty(logDirectory) ? DefaultFolder : logDirectory).GetApplicationResourcePath();
        if (!Directory.Exists(logDirectory))
        {
            Directory.CreateDirectory(logDirectory);
        }

        return logDirectory;
    }

    private static readonly ConcurrentDictionary<string, ILogger> s_namedLoggers = new();
    private static readonly Func<string, ILogger> s_namedLoggerBuilder = BuildNamedLogger;
    private readonly EventHandler<LoggingConfigurationChangedEventArgs>? _logManagerOnConfigurationChanged;

    private static ILogger BuildLogger(Type type)
        => new(new NLogLogger(type));
    private static ILogger BuildNamedLogger(string loggerName)
        => new(new NLogLogger(loggerName));

#if !ZK_EVM
    public ILogger GetClassLogger<T>() => TypedLogger<T>.Logger;
#endif

    public ILogger GetLogger(string loggerName) => s_namedLoggers.GetOrAdd(loggerName, s_namedLoggerBuilder);

    public void SetGlobalVariable(string name, object? value) => GlobalDiagnosticsContext.Set(name, value);

    private static void SetupLogRules(string? logRules)
    {
        //Add rules here for e.g. 'JsonRpc.*: Warn; Block.*: Error;',
        if (logRules is not null)
        {
            IList<LoggingRule> configurationLoggingRules = LogManager.Configuration.LoggingRules;
            lock (configurationLoggingRules)
            {
                Target[] targets = GetTargets(configurationLoggingRules);
                if (targets.Length == 0)
                {
                    return;
                }

                IEnumerable<LoggingRule> loggingRules = ParseRules(logRules, targets);
                foreach (LoggingRule loggingRule in loggingRules)
                {
                    RemoveOverriddenRules(configurationLoggingRules, loggingRule);
                    configurationLoggingRules.Add(loggingRule);
                }
            }
        }
    }

    /// <remarks>
    /// Excludes anything that reaches the seq target: its floor is <c>Seq.MinLevel</c>, which the runner's
    /// <c>NLogConfigurator</c> applies only to a catch-all ("*") rule whose target is named <c>seq</c> so that
    /// <c>NLog.config</c> can still opt a single namespace into seq with an explicit <c>writeTo="seq"</c> rule
    /// (#4835). A rule synthesised from <c>Init.LogRules</c> that fanned out to seq would bypass that floor
    /// (#6911), and removing a seq-writing rule as "overridden" would leave seq with no rule at all.
    /// </remarks>
    private static Target[] GetTargets(IList<LoggingRule> configurationLoggingRules) =>
        configurationLoggingRules.SelectMany(static r => r.Targets).Where(static t => !WritesToSeq(t)).Distinct().ToArray();

    /// <remarks>
    /// Never removes a rule that writes to seq, for the same reason <see cref="GetTargets"/> excludes it from
    /// the targets a synthesised rule can use — dropping it as "overridden" would leave seq with no rule at all.
    /// A surviving rule keeps its own levels and targets, so <c>Init.LogRules</c> cannot raise or lower a
    /// namespace that <c>NLog.config</c> already routes to seq, directly or through a group such as <c>all</c>,
    /// and a <c>final="true"</c> rule of that shape suppresses the synthesised rule entirely. That matches the
    /// node's behaviour with no <c>Init.LogRules</c> set: for those namespaces the config file is authoritative.
    /// </remarks>
    private static void RemoveOverriddenRules(IList<LoggingRule> configurationLoggingRules, LoggingRule loggingRule)
    {
        string regexPattern = $"^{loggingRule.LoggerNamePattern.Replace(".", "\\.").Replace("*", ".*")}$";
        for (int j = 0; j < configurationLoggingRules.Count;)
        {
            if (Regex.IsMatch(configurationLoggingRules[j].LoggerNamePattern, regexPattern) && !configurationLoggingRules[j].Targets.Any(WritesToSeq))
            {
                configurationLoggingRules.RemoveAt(j);
            }
            else
            {
                j++;
            }
        }
    }

    private static bool WritesToSeq(Target target) =>
        target.Name == SeqTargetName || target switch
        {
            WrapperTargetBase wrapper => wrapper.WrappedTarget is not null && WritesToSeq(wrapper.WrappedTarget),
            CompoundTargetBase compound => compound.Targets.Any(WritesToSeq),
            _ => false
        };

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

    public static void Shutdown() => LogManager.Shutdown();

    public void Dispose() => LogManager.ConfigurationChanged -= _logManagerOnConfigurationChanged;

    private static class TypedLogger<T>
    {
        public static ILogger Logger { get; } = BuildLogger(typeof(T));
    }
}
