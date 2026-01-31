// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.EngineApiProxy.Config;
using Nethermind.EngineApiProxy.Models;
using Nethermind.EngineApiProxy.Services;
using Nethermind.EngineApiProxy.Utilities;
using Nethermind.Logging;
using Newtonsoft.Json.Linq;

namespace Nethermind.EngineApiProxy.Handlers;

public class NewPayloadHandler(
    ProxyConfig config,
    RequestForwarder requestForwarder,
    MessageQueue messageQueue,
    PayloadTracker payloadTracker,
    RequestOrchestrator requestOrchestrator,
    ILogManager logManager)
{
    private const string ZeroHash = "0x0000000000000000000000000000000000000000000000000000000000000000";
    private const string ParentHashKey = "parentHash";
    private const string BlockHashKey = "blockHash";
    private const string HeadBlockHashKey = "headBlockHash";
    private const string FinalizedBlockHashKey = "finalizedBlockHash";
    private const string SafeBlockHashKey = "safeBlockHash";

    private readonly ProxyConfig _config = config;
    private readonly ILogger _logger = logManager.GetClassLogger();
    private readonly RequestForwarder _requestForwarder = requestForwarder;
    private readonly MessageQueue _messageQueue = messageQueue;
    private readonly PayloadTracker _payloadTracker = payloadTracker;
    private readonly RequestOrchestrator _requestOrchestrator = requestOrchestrator;

    public async Task<JsonRpcResponse> HandleRequest(JsonRpcRequest request)
    {
        _logger.Debug($"Processing NewPayload request {request.Id}");

        return _config.ValidationMode switch
        {
            ValidationMode.ForkChoiceUpdated => await ForwardWithoutValidation(request, "Validation disabled in FCU mode"),
            ValidationMode.Merged or ValidationMode.Lighthouse => await ProcessWithMergedValidation(request),
            ValidationMode.NewPayload when ShouldValidateBlock(request) => await ProcessWithValidation(request),
            _ => await _requestForwarder.ForwardRequestToExecutionClient(request)
        };
    }

    private async Task<JsonRpcResponse> ForwardWithoutValidation(JsonRpcRequest request, string reason)
    {
        _logger.Debug(reason);
        return await _requestForwarder.ForwardRequestToExecutionClient(request);
    }

    private bool ShouldValidateBlock(JsonRpcRequest request)
    {
        if (!_config.ValidateAllBlocks)
            return false;

        if (request.Params is not { Count: > 0 })
            return false;

        bool shouldValidate = HasNoPayloadAttributes(request.Params);

        _logger.Debug($"ValidateAllBlocks is enabled, params count: {request.Params.Count}, shouldValidate: {shouldValidate}");
        if (!shouldValidate)
        {
            _logger.Debug("Skipping validation because request already contains payload attributes");
        }

        return shouldValidate;
    }

    private async Task<JsonRpcResponse> ProcessWithValidation(JsonRpcRequest request)
    {
        _logger.Info("Validation enabled, pausing engine_newPayload original request");
        _messageQueue.PauseProcessing();

        try
        {
            string parentHash = request.Params is not null
                ? ExtractParentHash(request.Params)
                : string.Empty;

            if (string.IsNullOrEmpty(parentHash))
            {
                _logger.Warn("Could not extract parent hash, forwarding request as-is");
                return await _requestForwarder.ForwardRequestToExecutionClient(request);
            }

            _logger.Info($"Starting validation flow ({_config.ValidationMode} mode) for parent hash: {parentHash}");

            try
            {
                // Extract the block hash from the payload to use as headBlockHash
                string blockHash = ExtractBlockHashFromPayload(request);

                if (string.IsNullOrEmpty(blockHash))
                {
                    _logger.Warn("Could not extract blockHash from payload, using parentHash as fallback");
                    blockHash = parentHash;
                }

                // Extract blobVersionedHashes from incoming request (params[1]) and store them
                var parentHashObj = new Hash256(Bytes.FromHexString(parentHash));
                var incomingBlobHashes = BlobHashComputer.ExtractBlobVersionedHashes(request.Params);
                if (incomingBlobHashes.Count > 0)
                {
                    string[] hashArray = BlobHashComputer.ToStringArray(incomingBlobHashes);
                    _payloadTracker.AssociateBlobVersionedHashes(parentHashObj, hashArray);
                    _logger.Info($"Stored {hashArray.Length} blobVersionedHashes from incoming newPayload request for parent hash {parentHash}");
                }

                // Generate a synthetic FCU request
                _logger.Debug($"Generating synthetic FCU request using parentHash: {parentHash}");
                JsonRpcRequest fcuRequest = GenerateSyntheticFcuRequest(request, parentHash);

                // Copy headers from original request
                fcuRequest.OriginalHeaders = request.OriginalHeaders;

                // Use the existing FCU validation flow
                _logger.Debug("Using existing FCU validation flow with synthetic request");
                string payloadId = await _requestOrchestrator.HandleFCUWithValidation(fcuRequest, parentHash);

                // After validation, forward the original request to the execution client
                _logger.Info($"Validation flow with payloadId {payloadId} completed, forwarding original request to EL for actual response");
                return await _requestForwarder.ForwardRequestToExecutionClient(request);
            }
            catch (Exception ex)
            {
                // If the validation flow fails due to unsupported methods, log and fall back to normal flow
                if (ex.ToString().Contains("is not supported"))
                {
                    _logger.Warn($"Validation flow skipped due to unsupported methods: {ex.Message}");
                    _logger.Info("Falling back to direct forwarding of request to execution client");
                    return await _requestForwarder.ForwardRequestToExecutionClient(request);
                }

                // For other errors, throw to be caught by the outer try/catch
                throw;
            }
        }
        finally
        {
            // Always resume processing, even if there was an error
            _logger.Info("Resuming message queue processing");
            _messageQueue.ResumeProcessing();
        }
    }

    private async Task<JsonRpcResponse> ProcessWithMergedValidation(JsonRpcRequest request)
    {
        _logger.Info("Validation enabled, pausing engine_newPayload original request");
        _messageQueue.PauseProcessing();

        try
        {
            string blockHash = ExtractBlockHashFromPayload(request);
            string parentHash = request.Params is not null
                ? ExtractParentHash(request.Params)
                : string.Empty;

            if (string.IsNullOrEmpty(parentHash))
            {
                _logger.Warn($"Could not extract parent hash in {_config.ValidationMode} mode, forwarding request as-is");
                return await _requestForwarder.ForwardRequestToExecutionClient(request);
            }

            _logger.Info($"{_config.ValidationMode} validation for block with hash: {blockHash}, parent: {parentHash}");

            // In merged/LH mode, we should:
            // 1. Use parentHash to get payloadID from the tracker (stored during FCU)
            // 2. Call engine_getPayload with this payloadID
            // 3. Send a synthetic newPayload with the payload from getPayload
            // 4. Then forward the original newPayload request

            try
            {
                // Convert parentHash to Hash256 to look up in the payload tracker
                var parentHashObj = new Hash256(Bytes.FromHexString(parentHash));

                // Extract blobVersionedHashes from incoming request (params[1])
                var incomingBlobHashes = BlobHashComputer.ExtractBlobVersionedHashes(request.Params);
                if (incomingBlobHashes.Count > 0)
                {
                    // Store the blob versioned hashes for this block for later retrieval
                    string[] hashArray = BlobHashComputer.ToStringArray(incomingBlobHashes);
                    _payloadTracker.AssociateBlobVersionedHashes(parentHashObj, hashArray);
                    _logger.Info($"Stored {hashArray.Length} blobVersionedHashes from incoming newPayload request for parent hash {parentHash}");
                }

                // Try to get payloadId associated with this parent hash
                if (_payloadTracker.TryGetPayloadId(parentHashObj, out var payloadId) && !string.IsNullOrEmpty(payloadId))
                {
                    _logger.Info($"Found payloadId {payloadId} for parent hash {parentHash}, starting validation");

                    // Use the existing FCU validation flow
                    await _requestOrchestrator.DoValidationForFCU(payloadId, string.Empty);

                    _logger.Info($"Validation completed for parent hash {parentHash}, forwarding original request");
                }
                else
                {
                    _logger.Warn($"No payloadId found for parent hash {parentHash}, skipping validation. Last tracked payloadId: {_payloadTracker.LastTrackedPayloadId}, last block hash: {_payloadTracker.LastTrackedBlockHash}");
                }
            }
            catch (Exception ex)
            {
                // If the validation flow fails, log the error but continue with the original request
                _logger.Error($"Error in {_config.ValidationMode} validation flow: {ex.Message}", ex);
            }

            // Register this newPayload for future reference
            _payloadTracker.RegisterNewPayload(blockHash, parentHash);

            // Forward the original request to get the actual response
            var response = await _requestForwarder.ForwardRequestToExecutionClient(request);

            // Log the response status for monitoring
            if (response.Result is JObject result && result["status"] is not null)
            {
                string status = result["status"]?.ToString() ?? "unknown";
                _logger.Info($"{_config.ValidationMode} validation block {blockHash} status: {status}");
            }

            return response;
        }
        finally
        {
            // Always resume processing, even if there was an error
            _logger.Info($"Resuming message queue processing for {_config.ValidationMode} validation");
            _messageQueue.ResumeProcessing();
        }
    }

    private static JsonRpcRequest GenerateSyntheticFcuRequest(JsonRpcRequest originalRequest, string parentHash)
    {
        return originalRequest.Method switch
        {
            "engine_newPayloadV3" or "engine_newPayloadV4" => new JsonRpcRequest(
                "engine_forkchoiceUpdatedV3",
                new JArray(
                    new JObject
                    {
                        [HeadBlockHashKey] = parentHash,
                        [FinalizedBlockHashKey] = ZeroHash,
                        [SafeBlockHashKey] = ZeroHash
                    }
                ),
                Guid.NewGuid().ToString()
            ),
            _ => new JsonRpcRequest(
                "engine_forkchoiceUpdated",
                new JArray(
                    new JObject
                    {
                        [HeadBlockHashKey] = parentHash,
                        [FinalizedBlockHashKey] = ZeroHash,
                        [SafeBlockHashKey] = ZeroHash
                    }
                ),
                Guid.NewGuid().ToString()
            )
        };
    }

    private static string ExtractBlockHashFromPayload(JsonRpcRequest request)
    {
        if (request.Params is { Count: > 0 } && request.Params[0] is JObject payload)
        {
            return payload[BlockHashKey]?.ToString() ?? string.Empty;
        }
        return string.Empty;
    }

    /// <summary>
    /// Checks if the request has no payload attributes (empty, missing, or null second parameter).
    /// </summary>
    private static bool HasNoPayloadAttributes(JArray parameters)
    {
        if (parameters.Count == 1)
            return true;

        return parameters[1] switch
        {
            null => true,
            JToken { Type: JTokenType.Null } => true,
            JArray { Count: 0 } => true,
            JObject => false,
            _ => true
        };
    }

    /// <summary>
    /// Extracts the parent hash from the newPayload request parameters.
    /// The first parameter can be either:
    /// - A JObject containing the execution payload with a "parentHash" field
    /// - A JArray where the first element is a JObject with a "parentHash" field
    /// </summary>
    private static string ExtractParentHash(JArray parameters)
    {
        if (parameters is not { Count: > 0 })
            return string.Empty;

        return parameters[0] switch
        {
            JObject payload => payload[ParentHashKey]?.ToString() ?? string.Empty,
            JArray { Count: > 0 } arr when arr[0] is JObject nested => nested[ParentHashKey]?.ToString() ?? string.Empty,
            _ => string.Empty
        };
    }

    private static string ExtractParentBeaconBlockRoot(JArray parameters)
    {
        if (parameters is null || parameters.Count < 3)
            return string.Empty;

        // The parentBeaconBlockRoot is the third parameter in the NewPayloadV4 request
        var parentBeaconBlockRoot = parameters[2];
        if (parentBeaconBlockRoot is null || parentBeaconBlockRoot.Type == JTokenType.Null)
            return string.Empty;

        return parentBeaconBlockRoot.ToString();
    }
}
