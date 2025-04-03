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

        /// <summary>
        /// Whether to validate all blocks, even those where CL doesn't request validation
        /// </summary>
        public bool ValidateAllBlocks { get; set; } = false;

        /// <summary>
        /// Default fee recipient address to use when generating payload attributes
        /// </summary>
        public string DefaultFeeRecipient { get; set; } = "0x0000000000000000000000000000000000000000";

        /// <summary>
        /// Time offset in seconds for block timestamp calculation (default: 12s)
        /// </summary>
        public int TimestampOffsetSeconds { get; set; } = 12;

        public override string ToString()
        {
            return $"EC Endpoint: {ExecutionClientEndpoint}, Listen Port: {ListenPort}, Log Level: {LogLevel}, ValidateAllBlocks: {ValidateAllBlocks}";
        }
    }
} 