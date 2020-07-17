using System;
using System.Linq;
using NLog;
using NLog.Config;
using NLog.Targets.Seq;
using Microsoft.Extensions.CommandLineUtils;

namespace Nethermind.Runner
{
    public class NLogConfigurator
    {

        public NLogConfigurator() {
        }
        
        public void ConfigureSeqBufferTarget(
            string url = "http://localhost:5341", 
            string apiKey = "",
            string minLevel = "Off")
        {
            if (LogManager.Configuration?.AllTargets != null)
            {
                foreach (SeqTarget target in LogManager.Configuration.AllTargets.OfType<SeqTarget>())
                {
                    target.ApiKey = apiKey;
                    target.ServerUrl = url;
                    foreach(var rule in LogManager.Configuration.LoggingRules)
                    {
                        foreach (var ruleTarget in rule.Targets)
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
            LogManager.Configuration?.AllTargets.OfType<SeqTarget>().ToList().ForEach(t => t.Dispose());
            LogManager.ReconfigExistingLoggers();
        }

        public void ConfigureLogLevels(CommandOption logLevelOverride)
        {
            string logLevel = logLevelOverride.Value();
            NLog.LogLevel nLogLevel = NLog.LogLevel.Info;
            switch (logLevel.ToUpperInvariant())
            {
                case "OFF":
                    nLogLevel = NLog.LogLevel.Off;
                    break;
                case "ERROR":
                    nLogLevel = NLog.LogLevel.Error;
                    break;
                case "WARN":
                    nLogLevel = NLog.LogLevel.Warn;
                    break;
                case "INFO":
                    nLogLevel = NLog.LogLevel.Info;
                    break;
                case "DEBUG":
                    nLogLevel = NLog.LogLevel.Debug;
                    break;
                case "TRACE":
                    nLogLevel = NLog.LogLevel.Trace;
                    break;
            }

            Console.WriteLine($"Enabling log level override: {logLevel.ToUpperInvariant()}");

            foreach (LoggingRule rule in LogManager.Configuration.LoggingRules)
            {
                foreach (var ruleTarget in rule.Targets)
                {
                    if (ruleTarget.Name != "seq")
                    {
                        Console.WriteLine($"{ruleTarget.Name} TEST");
                        rule.DisableLoggingForLevels(NLog.LogLevel.Trace, nLogLevel);
                        rule.EnableLoggingForLevels(nLogLevel, NLog.LogLevel.Off);                    
                    }                        
                }
            }
            LogManager.ReconfigExistingLoggers();
        }
    }
}