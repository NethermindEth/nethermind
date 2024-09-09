// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using McMaster.Extensions.CommandLineUtils;
using Nethermind.Core.Collections;
using NLog;
using NLog.Config;
using NLog.Targets;
using NLog.Targets.Seq;

namespace Nethermind.Runner.Logging
{
    public static class NLogConfigurator
    {
        public static void ConfigureSeqBufferTarget(
            string url = "http://localhost:5341",
            string apiKey = "",
            string minLevel = "Off")
        {
            LoggingConfiguration loggingConfiguration = LogManager.Configuration;
            if (loggingConfiguration is not null)
            {
                if (loggingConfiguration.AllTargets is not null)
                {
                    foreach (SeqTarget target in loggingConfiguration.AllTargets.OfType<SeqTarget>())
                    {
                        target.ApiKey = apiKey;
                        target.ServerUrl = url;
                        foreach (LoggingRule? rule in loggingConfiguration.LoggingRules)
                        {
                            foreach (Target? ruleTarget in rule.Targets)
                            {
                                if (ruleTarget.Name == "seq" && rule.LoggerNamePattern == "*")
                                {
                                    rule.EnableLoggingForLevels(LogLevel.FromString(minLevel), LogLevel.Fatal);
                                }
                            }
                        }
                    }
                }

                // // // re-initialize single target
                loggingConfiguration.AllTargets?.OfType<SeqTarget>().ForEach(t => t.Dispose());
                LogManager.ReconfigExistingLoggers();
            }
        }

        public static void ClearSeqTarget()
        {
            LoggingConfiguration loggingConfiguration = LogManager.Configuration;
            loggingConfiguration?.RemoveTarget("seq");
        }

        public static void ConfigureLogLevels(CommandOption logLevelOverride)
        {
            string logLevel = logLevelOverride.Value();
            LogLevel nLogLevel = logLevel.ToUpperInvariant() switch
            {
                "OFF" => LogLevel.Off,
                "ERROR" => LogLevel.Error,
                "WARN" => LogLevel.Warn,
                "INFO" => LogLevel.Info,
                "DEBUG" => LogLevel.Debug,
                "TRACE" => LogLevel.Trace,
                _ => LogLevel.Info
            };

            Console.WriteLine($"Enabling log level override: {logLevel.ToUpperInvariant()}");

            // There are some rules for which we don't want to override the log level
            // but instead preserve the original config defined in the 'NLog.config' file
            string[] ignoredRuleNames =
            {
                "JsonWebAPI*",
                "JsonWebAPI.Microsoft.Extensions.Diagnostics.HealthChecks.DefaultHealthCheckService",
            };
            foreach (LoggingRule rule in LogManager.Configuration.LoggingRules)
            {
                if (ignoredRuleNames.Contains(rule.LoggerNamePattern)) { continue; }

                foreach (var ruleTarget in rule.Targets)
                {
                    if (ruleTarget.Name != "seq")
                    {
                        Console.WriteLine($"{ruleTarget.Name} TEST");
                        rule.DisableLoggingForLevels(LogLevel.Trace, nLogLevel);
                        rule.EnableLoggingForLevels(nLogLevel, LogLevel.Off);
                    }
                }
            }

            LogManager.ReconfigExistingLoggers();
        }
    }
}
