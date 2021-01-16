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
using System.Linq;
using Microsoft.Extensions.CommandLineUtils;
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
            if (loggingConfiguration != null)
            {
                if (loggingConfiguration.AllTargets != null)
                {
                    foreach (SeqTarget target in loggingConfiguration.AllTargets.OfType<SeqTarget>())
                    {
                        target.ApiKey = apiKey;
                        target.ServerUrl = url;
                        foreach (LoggingRule? rule in loggingConfiguration.LoggingRules)
                        {
                            foreach (Target? ruleTarget in rule.Targets)
                            {
                                if (ruleTarget.Name == "seq")
                                {
                                    rule.EnableLoggingForLevels(LogLevel.FromString(minLevel), LogLevel.Fatal);
                                }
                            }
                        }
                    }
                }
                
                // // // re-initialize single target
                loggingConfiguration.AllTargets?.OfType<SeqTarget>().ToList().ForEach(t => t.Dispose());
                LogManager.ReconfigExistingLoggers();
            }
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

            foreach (LoggingRule rule in LogManager.Configuration.LoggingRules)
            {
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
