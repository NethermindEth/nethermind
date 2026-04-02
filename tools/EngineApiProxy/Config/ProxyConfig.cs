// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.EngineApiProxy.Config;

/// <summary>
/// Mode for block validation flow.
/// </summary>
public enum ValidationMode
{
    /// <summary>
    /// Validation happens at ForkChoiceUpdated request.
    /// The proxy generates synthetic payload attributes and performs validation during FCU processing.
    /// </summary>
    ForkChoiceUpdated,

    /// <summary>
    /// Validation happens at NewPayload request.
    /// The proxy generates a synthetic FCU request when NewPayload arrives to trigger validation.
    /// </summary>
    NewPayload,

    /// <summary>
    /// FCU stores PayloadID and defers validation to the next NewPayload request.
    /// The proxy returns a synthetic response with null payloadId to the CL, then validates when NewPayload arrives.
    /// </summary>
    Merged,

    /// <summary>
    /// Intercepts PayloadAttributes from existing FCU requests sent by Lighthouse CL (with --always-prepare-payload).
    /// Most accurate mode as it uses real CL-provided attributes rather than synthetic ones.
    /// </summary>
    Lighthouse
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
    public string DefaultFeeRecipient { get; set; } = "0x1268ad189526ac0b386faf06effc46779c340ee6";

    /// <summary>
    /// Time offset in seconds for block timestamp calculation
    /// Should be more than 0 second to avoid conflicts with CL timestamp
    /// </summary>
    public int TimestampOffsetSeconds { get; set; } = 1;

    /// <summary>
    /// Timeout in seconds for HTTP requests to EL/CL clients
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 100;

    /// <summary>
    /// Mode for block validation (see ValidationMode enum for descriptions)
    /// </summary>
    public ValidationMode ValidationMode { get; set; } = ValidationMode.Lighthouse;

    /// <summary>
    /// Engine API method to use when getting payloads for validation
    /// </summary>
    public string GetPayloadMethod { get; set; } = "engine_getPayloadV4";

    /// <summary>
    /// Engine API method to use when sending new payloads for validation
    /// </summary>
    public string NewPayloadMethod { get; set; } = "engine_newPayloadV4";

    public override string ToString()
    {
        return $"EC Endpoint: {ExecutionClientEndpoint}, CL Endpoint: {ConsensusClientEndpoint ?? "not set"}, Listen Port: {ListenPort}, Log Level: {LogLevel}, LogFile: {LogFile ?? "console only"}, ValidateAllBlocks: {ValidateAllBlocks}, ValidationMode: {ValidationMode}, GetPayloadMethod: {GetPayloadMethod}, NewPayloadMethod: {NewPayloadMethod}, RequestTimeout: {RequestTimeoutSeconds}s";
    }
}
