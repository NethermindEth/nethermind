namespace Nethermind.EngineApiProxy.Config
{
    public enum ValidationMode
    {
        Fcu,
        NewPayload,
        Merged
    }

    public class ProxyConfig
    {
        /// <summary>
        /// The endpoint URL of the execution client to forward requests to
        /// </summary>
        public string? ExecutionClientEndpoint { get; set; }

        /// <summary>
        /// The endpoint URL of the consensus client to forward requests to (optional)
        /// </summary>
        public string? ConsensusClientEndpoint { get; set; }

        /// <summary>
        /// Port to listen for incoming requests from the consensus client
        /// </summary>
        public int ListenPort { get; set; } = 8551;

        /// <summary>
        /// Logging verbosity level
        /// </summary>
        public string LogLevel { get; set; } = "Info";

        /// <summary>
        /// Path to log file (null means console logging only)
        /// </summary>
        public string? LogFile { get; set; } = null;

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
        /// Should be more than 0 second to avoid conflicts with CL timestamp
        /// </summary>
        public int TimestampOffsetSeconds { get; set; } = 1;

        /// <summary>
        /// Mode for block validation:
        /// - Fcu: Validation happens at FCU time
        /// - NewPayload: Validation happens at new payload time
        /// - Merged: FCU stores PayloadID without validation, and validation happens at next new_payload request
        /// </summary>
        public ValidationMode ValidationMode { get; set; } = ValidationMode.NewPayload;

        public override string ToString()
        {
            return $"EC Endpoint: {ExecutionClientEndpoint}, CL Endpoint: {ConsensusClientEndpoint ?? "not set"}, Listen Port: {ListenPort}, Log Level: {LogLevel}, LogFile: {LogFile ?? "console only"}, ValidateAllBlocks: {ValidateAllBlocks}, ValidationMode: {ValidationMode}";
        }
    }
} 