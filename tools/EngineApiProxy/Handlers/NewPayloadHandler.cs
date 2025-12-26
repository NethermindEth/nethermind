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
    private readonly ProxyConfig _config = config ?? throw new ArgumentNullException(nameof(config));
    private readonly ILogger _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
    private readonly RequestForwarder _requestForwarder = requestForwarder ?? throw new ArgumentNullException(nameof(requestForwarder));
    private readonly MessageQueue _messageQueue = messageQueue ?? throw new ArgumentNullException(nameof(messageQueue));
    private readonly PayloadTracker _payloadTracker = payloadTracker ?? throw new ArgumentNullException(nameof(payloadTracker));
    private readonly RequestOrchestrator _requestOrchestrator = requestOrchestrator ?? throw new ArgumentNullException(nameof(requestOrchestrator));

    public async Task<JsonRpcResponse> HandleRequest(JsonRpcRequest request)
    {
        _logger.Debug($"Processing NewPayload request {request.Id}");

        // Check if the request should be validated
        if (_config.ValidationMode == ValidationMode.Fcu)
        {
            _logger.Debug("Validation for NewPayload disabled in FCU mode");
            return await _requestForwarder.ForwardRequestToExecutionClient(request);
        }

        if (_config.ValidationMode == ValidationMode.Merged || _config.ValidationMode == ValidationMode.LH)
        {
            _logger.Debug($"Processing NewPayload in {_config.ValidationMode} validation mode");
            return await ProcessWithMergedValidation(request);
        }

        // Check if we should validate this request
        bool shouldValidate = ShouldValidateBlock(request);
        _logger.Info($"ShouldValidateBlock for NewPayload: {shouldValidate}");

        if (shouldValidate)
        {
            return await ProcessWithValidation(request);
        }

        // Directly forward to EC without validation
        return await _requestForwarder.ForwardRequestToExecutionClient(request);
    }

    private bool ShouldValidateBlock(JsonRpcRequest request)
    {
        bool shouldValidate = _config.ValidateAllBlocks &&
                             request.Params is not null &&
                             request.Params.Count > 0 &&
                             IsEmptyOrMissingPayloadAttributes(request.Params);

        // Add detailed logging to show validation decision
        if (_config.ValidateAllBlocks)
        {
            _logger.Debug($"ValidateAllBlocks is enabled, params count: {request.Params?.Count}, second param type: {(request.Params?.Count > 1 ? request.Params[1]?.GetType().Name : "none")}");

            if (request.Params?.Count > 1 && request.Params[1] is JObject)
            {
                _logger.Debug("Skipping validation because request already contains payload attributes");
            }
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

    private async Task<JsonRpcResponse> ProcessWithLHValidation(JsonRpcRequest request)
    {
        // Log that we're starting validation for this block
        var blockHash = ExtractBlockHashFromPayload(request);
        var parentHash = request.Params is not null
            ? ExtractParentHash(request.Params)
            : string.Empty;

        // Extract parent beacon block root if available in the request
        var parentBeaconBlockRoot = request.Params is not null
            ? ExtractParentBeaconBlockRoot(request.Params)
            : string.Empty;

        _logger.Info($"LH validation for block with hash: {blockHash}, parent: {parentHash}, parentBeaconBlockRoot: {parentBeaconBlockRoot}");

        // Check if we have a payloadId for the parent hash, indicating that this was from a previous FCU
        var parentHashObj = new Hash256(Bytes.FromHexString(parentHash));
        if (_payloadTracker.TryGetPayloadId(parentHashObj, out var payloadId) && !string.IsNullOrEmpty(payloadId))
        {
            _logger.Info($"Found payloadId {payloadId} for parent hash {parentHash}, starting validation");

            // If parentBeaconBlockRoot is not provided in the request, try to get it from the tracker
            if (string.IsNullOrEmpty(parentBeaconBlockRoot) &&
                _payloadTracker.TryGetParentBeaconBlockRoot(parentHashObj, out var trackedParentBeaconBlockRoot) &&
                !string.IsNullOrEmpty(trackedParentBeaconBlockRoot))
            {
                parentBeaconBlockRoot = trackedParentBeaconBlockRoot;
                _logger.Info($"ProcessWithLHValidation: Using parentBeaconBlockRoot {parentBeaconBlockRoot} from payload tracker for parent hash {parentHash}");
            }

            try
            {
                // First check if we should run validation by comparing block hashes
                bool shouldValidate = await _requestOrchestrator.ValidatePayloadWithBlockHashCheck(payloadId, blockHash);

                if (shouldValidate)
                {
                    // Run the validation only if the previous check passed
                    // Pass parentBeaconBlockRoot from the original request to ensure it's used
                    bool validationResult = await _requestOrchestrator.DoValidationForFCU(payloadId, parentBeaconBlockRoot);

                    if (validationResult)
                    {
                        _logger.Info($"LH validation successful for payloadId {payloadId}");
                    }
                    else
                    {
                        _logger.Warn($"LH validation failed for payloadId {payloadId}, but still forwarding original request");
                    }
                }
                else
                {
                    _logger.Info($"Skipping validation due to potential block hash mismatch, forwarding original request");
                }
            }
            catch (Exception ex)
            {
                _logger.Warn($"Error during LH validation: {ex.Message}. Falling back to direct forwarding.");
            }
        }
        else
        {
            _logger.Info($"No payloadId found for parent hash {parentHash}, forwarding request as-is");
        }

        // Register this payload for future reference
        _payloadTracker.RegisterNewPayload(blockHash, parentHash);

        // Forward the original request to the execution client
        return await _requestForwarder.ForwardRequestToExecutionClient(request);
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
                        ["headBlockHash"] = parentHash,
                        ["finalizedBlockHash"] = "0x0000000000000000000000000000000000000000000000000000000000000000",
                        ["safeBlockHash"] = "0x0000000000000000000000000000000000000000000000000000000000000000"
                    }
                ),
                Guid.NewGuid().ToString()
            ),
            _ => new JsonRpcRequest(
                "engine_forkchoiceUpdated",
                new JArray(
                    new JObject
                    {
                        ["headBlockHash"] = parentHash,
                        ["finalizedBlockHash"] = "0x0000000000000000000000000000000000000000000000000000000000000000",
                        ["safeBlockHash"] = "0x0000000000000000000000000000000000000000000000000000000000000000"
                    }
                ),
                Guid.NewGuid().ToString()
            )
        };
    }

    private static string ExtractBlockHashFromPayload(JsonRpcRequest request)
    {
        if (request.Params != null && request.Params.Count > 0 && request.Params[0] is JObject payload)
        {
            return payload["blockHash"]?.ToString() ?? string.Empty;
        }
        return string.Empty;
    }

    private static bool IsEmptyOrMissingPayloadAttributes(JArray parameters)
    {
        // No second parameter
        if (parameters.Count == 1)
            return true;

        // Second parameter is null, empty array, or has a null type
        var secondParam = parameters[1];
        if (secondParam is null)
            return true;

        if (secondParam.Type == JTokenType.Null)
            return true;

        if (secondParam is JArray arr && arr.Count == 0)
            return true;

        // Not a JObject (which would contain payload attributes)
        return !(secondParam is JObject);
    }

    private static string ExtractParentHash(JArray parameters)
    {
        if (parameters is null || parameters.Count == 0)
            return string.Empty;

        var param = parameters[0];
        if (param is null)
            return string.Empty;

        if (param is JObject payload && payload["parentHash"] is not null)
            return payload["parentHash"]?.ToString() ?? string.Empty;

        if (param is JArray array &&
            array.Count > 0 &&
            array[0] is JObject obj &&
            obj["parentHash"] is not null)
            return obj["parentHash"]?.ToString() ?? string.Empty;

        return string.Empty;
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
