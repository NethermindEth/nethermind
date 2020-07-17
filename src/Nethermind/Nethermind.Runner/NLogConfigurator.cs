using System.Reflection;
using NLog;
using NLog.Config;
using NLog.Targets.Seq;
using NLog.Targets.Wrappers;

namespace Nethermind.Runner
{
    public class NLogConfigurator
    {

        public NLogConfigurator() {
            var assembly = Assembly.Load("NLog.Targets.Seq");
            NLog.Config.ConfigurationItemFactory.Default.RegisterItemsFromAssembly(assembly);
        }
        
        public void ConfigureSeqBufferTarget(LogLevel level,
            string url = "http://localhost:5341", 
            string apiKey = "",
            int bufferSize= 1000,
            int flushTimeout= 2000)
        {
            SeqTarget seqTarget = new SeqTarget();
            SeqPropertyItem prop;

            var config = LogManager.Configuration;

            seqTarget.ApiKey = apiKey;
            seqTarget.ServerUrl = url;

            // Defining properties for Seq Target
            prop = new SeqPropertyItem();
            prop.Name = "ThreadId";
            prop.Value = "${threadid}";
            seqTarget.Properties.Add(prop);

            prop = new SeqPropertyItem();
            prop.Name = "MachineName";
            prop.Value = "${machinename}";
            seqTarget.Properties.Add(prop);

            prop = new SeqPropertyItem();
            prop.Name = "Logger";
            prop.Value = "${logger}";
            seqTarget.Properties.Add(prop);

            prop = new SeqPropertyItem();
            prop.Name = "Exception";
            prop.Value = "${exception}";
            seqTarget.Properties.Add(prop);

            prop = new SeqPropertyItem();
            prop.Name = "Enode";
            prop.Value = "${gdc:item=enode}";
            seqTarget.Properties.Add(prop);

            prop = new SeqPropertyItem();
            prop.Name = "Chain";
            prop.Value = "${gdc:item=chain}";
            seqTarget.Properties.Add(prop);

            prop = new SeqPropertyItem();
            prop.Name = "ChainID";
            prop.Value = "${gdc:item=chainId}";
            seqTarget.Properties.Add(prop);

            prop = new SeqPropertyItem();
            prop.Name = "Engine";
            prop.Value = "${gdc:item=engine}";
            seqTarget.Properties.Add(prop);

            prop = new SeqPropertyItem();
            prop.Name = "NodeName";
            prop.Value = "${gdc:item=nodeName}";
            seqTarget.Properties.Add(prop);

            prop = new SeqPropertyItem();
            prop.Name = "Version";
            prop.Value = "${gdc:item=version}";
            seqTarget.Properties.Add(prop);

            var bufferWrapper = new BufferingTargetWrapper
            {
                Name = "seq",
                BufferSize = bufferSize,
                FlushTimeout = flushTimeout,
                WrappedTarget = seqTarget
            };

            config.AddTarget(bufferWrapper);

            var rule = new LoggingRule("*", level, bufferWrapper);
            config.LoggingRules.Add(rule);
            

            // Activate the configuration
            LogManager.Configuration = config;
        }
    }
}