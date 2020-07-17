using System;
using System.Reflection;
using System.IO;
using System.Linq;
using NLog;
using NLog.Config;
using NLog.Targets.Seq;
using NLog.Targets.Wrappers;


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
                                rule.EnableLoggingForLevel(LogLevel.FromString(minLevel));
                            }                        
                        }
                    }
                }
            }
            // re-initialize single target
            LogManager.Configuration?.AllTargets.OfType<SeqTarget>().ToList().ForEach(t => t.Dispose());
            LogManager.ReconfigExistingLoggers();
        }
    }
}