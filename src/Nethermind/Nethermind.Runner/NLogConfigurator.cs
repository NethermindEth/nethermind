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
        
        public void ConfigureSeqBufferTarget(LogLevel level,
            string url = "http://localhost:5341", 
            string apiKey = "")
        {
            if (LogManager.Configuration?.AllTargets != null)
            {
                foreach (SeqTarget target in LogManager.Configuration.AllTargets.OfType<SeqTarget>())
                {
                    target.ApiKey = apiKey;
                    target.ServerUrl = url;
                }
            }
            // re-initialize single target
            LogManager.Configuration?.AllTargets.OfType<SeqTarget>().ToList().ForEach(t => t.Dispose());
            LogManager.ReconfigExistingLoggers();
        }
    }
}