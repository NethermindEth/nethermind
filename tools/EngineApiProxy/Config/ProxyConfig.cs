namespace Nethermind.EngineApiProxy.Config
{
    public class ProxyConfig
    {
        /// <summary>
        /// The endpoint URL of the execution client to forward requests to
        /// </summary>
        public string? ExecutionClientEndpoint { get; set; }

        /// <summary>
        /// Port to listen for incoming requests from the consensus client
        /// </summary>
        public int ListenPort { get; set; } = 8551;

        /// <summary>
        /// Logging verbosity level
        /// </summary>
        public string LogLevel { get; set; } = "Info";

        public override string ToString()
        {
            return $"EC Endpoint: {ExecutionClientEndpoint}, Listen Port: {ListenPort}, Log Level: {LogLevel}";
        }
    }
} 