// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.EngineApiProxy.Config;
using Nethermind.EngineApiProxy.Models;
using Nethermind.EngineApiProxy.Services;
using Nethermind.EngineApiProxy.Utilities;
using Nethermind.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Nethermind.EngineApiProxy.Handlers;

public class ForkChoiceUpdatedHandler : IDisposable
{
    private readonly ProxyConfig _config;
    private readonly ILogger _logger;
    private readonly RequestForwarder _requestForwarder;
    private readonly MessageQueue _messageQueue;
    private readonly PayloadTracker _payloadTracker;
    private readonly RequestOrchestrator _requestOrchestrator;

    // Cache for LH mode to avoid duplicate FCU requests to EL
    private readonly ConcurrentDictionary<string, JsonRpcResponse> _lhResponseCache = new();
    private readonly ConcurrentDictionary<string, DateTime> _lhCacheTimestamps = new();
    private readonly TimeSpan _lhCacheExpiryTime = TimeSpan.FromSeconds(30);
    private readonly System.Threading.Timer _cacheCleanupTimer;
    private bool _disposed;

    public ForkChoiceUpdatedHandler(
        ProxyConfig config,
        RequestForwarder requestForwarder,
        MessageQueue messageQueue,
        PayloadTracker payloadTracker,
        RequestOrchestrator requestOrchestrator,
        ILogManager logManager)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        _requestForwarder = requestForwarder ?? throw new ArgumentNullException(nameof(requestForwarder));
        _messageQueue = messageQueue ?? throw new ArgumentNullException(nameof(messageQueue));
        _payloadTracker = payloadTracker ?? throw new ArgumentNullException(nameof(payloadTracker));
        _requestOrchestrator = requestOrchestrator ?? throw new ArgumentNullException(nameof(requestOrchestrator));

        // Start cache cleanup timer (every 30 seconds)
        _cacheCleanupTimer = new System.Threading.Timer(CleanupExpiredCacheEntries, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public async Task<JsonRpcResponse> HandleRequest(JsonRpcRequest request)
    {
        if (_config.ValidationMode == ValidationMode.NewPayload)
        {
            _logger.Debug($"Processing engine_forkchoiceUpdated (NewPayload flow): {request}");
            return await _requestForwarder.ForwardRequestToExecutionClient(request);
        }
        else if (_config.ValidationMode == ValidationMode.Merged)
        {
            // Log the request
            _logger.Debug($"Processing engine_forkchoiceUpdated (Merged flow): {request}");

            try
            {
                // Check if we should validate this block
                bool shouldValidate = ShouldValidateBlock(request);
                _logger.Info($"---------------(Merged flow - FCU processing - Validation: {shouldValidate})-----------------");

                // For Merged mode:
                // 1. Send FCU with payload attributes to EL
                // 2. Store the payloadID from the response
                // 3. Return FCU response without payloadID to CL
                return await ProcessMergedFCU(request);
            }
            catch (Exception ex)
            {
                _logger.Error($"Error handling forkChoiceUpdated in Merged mode: {ex.Message}", ex);
                return JsonRpcResponse.CreateErrorResponse(request.Id, -32603, $"Proxy error handling forkChoiceUpdated: {ex.Message}");
            }
        }
        else if (_config.ValidationMode == ValidationMode.LH)
        {
            // Log the request

            _logger.Debug($"Processing engine_forkchoiceUpdated (LH flow): {request}");

            try
            {
                // Check if we should validate this block
                // TODO: Fix ShouldValidateBlock for LH mode
                bool shouldValidate = ShouldValidateBlock(request);
                _logger.Info($"---------------(LH flow - FCU processing - Validation: {!shouldValidate})-----------------");

                // For LH mode:
                // 1. Intercept existing PayloadAttributes from FCU request (if present)
                // 2. Forward FCU with intercepted PayloadAttributes to EL
                // 3. Store the payloadID from the response
                // 4. Return FCU response without payloadID to CL
                return await ProcessLHFCU(request);
            }
            catch (Exception ex)
            {
                _logger.Error($"Error handling forkChoiceUpdated in LH mode: {ex.Message}", ex);
                return JsonRpcResponse.CreateErrorResponse(request.Id, -32603, $"Proxy error handling forkChoiceUpdated: {ex.Message}");
            }
        }

        // Log the forkChoiceUpdated request

        _logger.Debug($"Processing engine_forkchoiceUpdated (FCU flow): {request}");

        try
        {
            // Check if we should validate this block
            bool shouldValidate = ShouldValidateBlock(request);
            _logger.Info($"---------------(FCU flow - FCU processing - Validation: {shouldValidate})-----------------");

            if (shouldValidate)
            {
                return await ProcessWithValidation(request);
            }

            // Forward the request to EC without modification if not validating
            // TODO: Add empty payloadID for merged mode?
            return await ProcessWithoutValidation(request);
        }
        catch (Exception ex)
        {
            _logger.Error($"Error handling forkChoiceUpdated: {ex.Message}", ex);
            return JsonRpcResponse.CreateErrorResponse(request.Id, -32603, $"Proxy error handling forkChoiceUpdated: {ex.Message}");
        }
    }

    private bool ShouldValidateBlock(JsonRpcRequest request)
    {
        bool shouldValidate = _config.ValidateAllBlocks &&
                              request.Params is not null &&
                              request.Params.Count > 0 &&
                              (request.Params.Count == 1 ||
                               request.Params[1] is null ||
                               (request.Params[1] is JValue jv && jv.Type == JTokenType.Null) ||
                               (request.Params[1]?.Type == JTokenType.Null)) &&
                              !(request.Params.Count > 1 && request.Params[1] is JObject);

        // Add detailed logging to show validation decision
        if (_config.ValidateAllBlocks)
        {
            _logger.Debug($"ValidateAllBlocks is enabled, params count: {request.Params?.Count}, second param type: {(request.Params?.Count > 1 ? request.Params[1]?.GetType().Name : "none")}");

            if (request.Params?.Count > 1 && request.Params[1] is JObject)
            {
                _logger.Debug($"Skipping validation because request already contains payload attributes. shouldValidate: {shouldValidate}");
            }
        }

        return shouldValidate;
    }

    private async Task<JsonRpcResponse> ProcessWithValidation(JsonRpcRequest request)
    {
        _logger.Info("Validation enabled, pausing engine_forkChoiceUpdated original request");
        _messageQueue.PauseProcessing();

        try
        {
            string headBlockHashStr = ExtractHeadBlockHash(request);
            if (string.IsNullOrEmpty(headBlockHashStr))
            {
                _logger.Warn("Could not extract head block hash, forwarding request as-is");
                return await _requestForwarder.ForwardRequestToExecutionClient(request);
            }

            _logger.Info($"Starting validation flow ({_config.ValidationMode} mode) for head block: {headBlockHashStr}");

            try
            {
                // Use the orchestrator to handle the validation flow
                string payloadId = await _requestOrchestrator.HandleFCUWithValidation(request, headBlockHashStr);

                if (string.IsNullOrEmpty(payloadId))
                {
                    _logger.Warn("Validation flow failed, payloadId is empty. Seems like the node is not synced yet.");
                }
                else
                {
                    _logger.Info($"Validation flow for payloadId {payloadId} completed successfully, forwarding original FCU request to EL for actual response");
                }
                return await _requestForwarder.ForwardRequestToExecutionClient(request);
            }
            catch (Exception ex)
            {
                // If the validation flow fails due to unsupported methods, log and fall back to normal flow
                if (ex.ToString().Contains("is not supported") || ex.ToString().Contains("is not implemented"))
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

    private async Task<JsonRpcResponse> ProcessWithoutValidation(JsonRpcRequest request)
    {
        var response = await _requestForwarder.ForwardRequestToExecutionClient(request);

        // If response contains payloadId, store it for tracking
        if (response.Result is JObject resultObj &&
            resultObj["payloadId"] is not null &&
            request.Params is not null &&
            request.Params.Count > 0 &&
            request.Params[0] is JObject fcState &&
            fcState["headBlockHash"] is not null)
        {
            string payloadId = resultObj["payloadId"]?.ToString() ?? string.Empty;
            string headBlockHashStr = fcState["headBlockHash"]?.ToString() ?? string.Empty;

            if (!string.IsNullOrEmpty(payloadId) && !string.IsNullOrEmpty(headBlockHashStr))
            {
                var headBlockHash = new Hash256(Bytes.FromHexString(headBlockHashStr));
                _payloadTracker.TrackPayload(headBlockHash, payloadId);
                _logger.Debug($"Tracked payloadId {payloadId} for head block {headBlockHash}");
            }
        }

        return response;
    }

    private async Task<JsonRpcResponse> ProcessMergedFCU(JsonRpcRequest request)
    {
        _logger.Debug("Processing merged FCU flow");

        string headBlockHashStr = string.Empty;
        string payloadId = string.Empty;

        try
        {
            headBlockHashStr = ExtractHeadBlockHash(request);
            if (string.IsNullOrEmpty(headBlockHashStr))
            {
                _logger.Warn("Could not extract head block hash in merged mode, forwarding request as-is");
                return await _requestForwarder.ForwardRequestToExecutionClient(request);
            }

            _logger.Info($"Starting merged validation flow for head block: {headBlockHashStr}");

            // 1. Get payload ID but don't validate yet
            payloadId = await _requestOrchestrator.GetPayloadID(request, headBlockHashStr, true);

            if (string.IsNullOrEmpty(payloadId))
            {
                _logger.Warn("Merged validation flow failed to get payloadId, returning response without it");
            }
            else
            {
                _logger.Info($"Merged validation flow successfully got payloadId {payloadId}");
                // Track this payload ID with the hash so it can be found later
                var headBlockHash = new Hash256(Bytes.FromHexString(headBlockHashStr));
                _payloadTracker.TrackPayload(headBlockHash, payloadId);
            }

            // 2. Create a response without payloadId to return to CL
            var response = new JsonRpcResponse
            {
                Id = request.Id,
                Result = new JObject
                {
                    ["payloadStatus"] = new JObject
                    {
                        ["status"] = "VALID",
                        ["latestValidHash"] = headBlockHashStr,
                        ["validationError"] = null
                    },
                    ["payloadId"] = null
                }
            };

            return response;
        }
        catch (Exception ex)
        {
            _logger.Error($"Error handling merged FCU: {ex.Message}", ex);
            return JsonRpcResponse.CreateErrorResponse(request.Id, -32603, $"Proxy error handling merged FCU: {ex.Message}");
        }
    }

    private async Task<JsonRpcResponse> ProcessLHFCU(JsonRpcRequest request)
    {
        _logger.Debug("Processing LH FCU flow");

        try
        {
            // Check if request contains payload attributes
            bool hasPayloadAttributes = request.Params is not null &&
                                      request.Params.Count > 1 &&
                                      request.Params[1] is JObject;

            // For requests with payload attributes, implement deduplication
            if (hasPayloadAttributes)
            {
                _logger.Info("LH mode: got FCU request with payload attributes, processing validation flow");
                // Create a fingerprint of the request to identify duplicates
                string requestFingerprint = ComputeRequestFingerprint(request);

                // Check if we've seen this exact request recently
                if (_lhResponseCache.TryGetValue(requestFingerprint, out var cachedResponse))
                {
                    if (_lhCacheTimestamps.TryGetValue(requestFingerprint, out var timestamp))
                    {
                        if (DateTime.UtcNow - timestamp < _lhCacheExpiryTime)
                        {
                            _logger.Info($"LH mode: Duplicate FCU request detected (fingerprint: {GetSafeSubstring(requestFingerprint, 0, 10)}...), returning cached response to CL");

                            // Update ID to match the current request
                            var clonedResponse = CloneResponseWithNewId(cachedResponse, request.Id);
                            return clonedResponse;
                        }
                        else
                        {
                            // Cache entry expired, remove it
                            _logger.Debug($"LH mode: Cache entry expired for fingerprint: {GetSafeSubstring(requestFingerprint, 0, 10)}..., will forward to EL");
                            _lhResponseCache.TryRemove(requestFingerprint, out _);
                            _lhCacheTimestamps.TryRemove(requestFingerprint, out _);
                        }
                    }
                }

                _logger.Info($"LH mode: New unique FCU request with payload attributes (fingerprint: {GetSafeSubstring(requestFingerprint, 0, 10)}...)");

                // Extract the head block hash for tracking
                string headBlockHashStr = string.Empty;
                if (request.Params is not null &&
                    request.Params.Count > 0 &&
                    request.Params[0] is JObject fcState &&
                    fcState["headBlockHash"] is not null)
                {
                    headBlockHashStr = fcState["headBlockHash"]?.ToString() ?? string.Empty;
                }

                // Extract parent beacon block root if available
                string? extractedParentBeaconBlockRoot = ExtractParentBeaconBlockRoot(request);

                // Store parentBeaconBlockRoot for this head block hash even before we have a payloadId
                if (!string.IsNullOrEmpty(headBlockHashStr) && !string.IsNullOrEmpty(extractedParentBeaconBlockRoot))
                {
                    try
                    {
                        var headBlockHash = new Hash256(Bytes.FromHexString(headBlockHashStr));
                        _payloadTracker.AssociateParentBeaconBlockRoot(headBlockHash, extractedParentBeaconBlockRoot);
                        _logger.Debug($"Pre-emptively stored parentBeaconBlockRoot {extractedParentBeaconBlockRoot} for head block {headBlockHash}");
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"Failed to pre-emptively store parentBeaconBlockRoot: {ex.Message}");
                    }
                }

                // Forward the original request to EL
                var response = await _requestForwarder.ForwardRequestToExecutionClient(request);

                // If response contains payloadId, store it for tracking
                if (response.Result is JObject resultObj &&
                    resultObj["payloadId"] is not null)
                {
                    string payloadId = resultObj["payloadId"]?.ToString() ?? string.Empty;

                    if (!string.IsNullOrEmpty(payloadId) && !string.IsNullOrEmpty(headBlockHashStr))
                    {
                        // Track this payload ID with the hash so it can be found later
                        var headBlockHash = new Hash256(Bytes.FromHexString(headBlockHashStr));

                        // Extract parent beacon block root if available in request
                        string? parentBeaconBlockRoot = ExtractParentBeaconBlockRoot(request);
                        if (!string.IsNullOrEmpty(parentBeaconBlockRoot))
                        {
                            _logger.Info($"Storing parentBeaconBlockRoot {parentBeaconBlockRoot} with payloadId {payloadId} for head block {headBlockHashStr}");
                            _payloadTracker.TrackPayload(headBlockHash, payloadId, parentBeaconBlockRoot);
                        }
                        else
                        {
                            _payloadTracker.TrackPayload(headBlockHash, payloadId);
                        }

                        // Only cache the response if we have less than 100 entries (prevent memory issues)
                        if (_lhResponseCache.Count < 100)
                        {
                            _lhResponseCache[requestFingerprint] = response;
                            _lhCacheTimestamps[requestFingerprint] = DateTime.UtcNow;
                            _logger.Debug($"LH mode: Cached response for FCU request (fingerprint: {GetSafeSubstring(requestFingerprint, 0, 10)}...)");
                        }
                        else
                        {
                            _logger.Warn($"LH mode: Cache full (size: {_lhResponseCache.Count}), not caching response");
                        }
                    }
                    else
                    {
                        _logger.Warn("LH validation flow received response but payloadId or headBlockHash is empty");
                    }
                }
                else
                {
                    _logger.Warn("LH validation flow received response with no payloadId");
                }

                return response;
            }
            else
            {
                // For requests without payload attributes, just forward as usual
                _logger.Info("LH mode: FCU request without payload attributes, forwarding normally. This usually means the node is not synced yet.");

                // Forward the original request to EL
                var response = await _requestForwarder.ForwardRequestToExecutionClient(request);

                // Process response normally
                if (response.Result is JObject resultObj &&
                    resultObj["payloadId"] is not null &&
                    request.Params is not null &&
                    request.Params.Count > 0 &&
                    request.Params[0] is JObject fcState &&
                    fcState["headBlockHash"] is not null)
                {
                    string payloadId = resultObj["payloadId"]?.ToString() ?? string.Empty;
                    string headBlockHashStr = fcState["headBlockHash"]?.ToString() ?? string.Empty;

                    if (!string.IsNullOrEmpty(payloadId) && !string.IsNullOrEmpty(headBlockHashStr))
                    {
                        // Track this payload ID with the hash so it can be found later
                        var headBlockHash = new Hash256(Bytes.FromHexString(headBlockHashStr));

                        // Extract parent beacon block root if available in request
                        string? parentBeaconBlockRoot = ExtractParentBeaconBlockRoot(request);
                        if (!string.IsNullOrEmpty(parentBeaconBlockRoot))
                        {
                            _logger.Info($"Storing parentBeaconBlockRoot {parentBeaconBlockRoot} with payloadId {payloadId} for head block {headBlockHashStr}");
                            _payloadTracker.TrackPayload(headBlockHash, payloadId, parentBeaconBlockRoot);
                        }
                        else
                        {
                            _payloadTracker.TrackPayload(headBlockHash, payloadId);
                        }
                    }
                }

                return response;
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Error handling LH FCU: {ex.Message}", ex);
            return JsonRpcResponse.CreateErrorResponse(request.Id, -32603, $"Proxy error handling LH FCU: {ex.Message}");
        }
    }

    /// <summary>
    /// Computes a fingerprint for FCU requests to identify duplicates
    /// </summary>
    private static string ComputeRequestFingerprint(JsonRpcRequest request)
    {
        // We only need the params to determine if two requests are identical
        if (request.Params is null)
            return string.Empty;

        // Create a normalized JSON string of the params
        return JsonConvert.SerializeObject(request.Params);
    }

    /// <summary>
    /// Creates a clone of a response with a new ID to match the current request
    /// </summary>
    private static JsonRpcResponse CloneResponseWithNewId(JsonRpcResponse response, object? newId)
    {
        return new JsonRpcResponse
        {
            Id = newId,
            Result = response.Result is not null
                ? JsonConvert.DeserializeObject<JToken>(JsonConvert.SerializeObject(response.Result))
                : null,
            Error = response.Error is not null
                ? new JsonRpcError
                {
                    Code = response.Error.Code,
                    Message = response.Error.Message,
                    Data = response.Error.Data is not null
                        ? JsonConvert.DeserializeObject<JToken>(JsonConvert.SerializeObject(response.Error.Data))
                        : null
                }
                : null
        };
    }

    private static string ExtractHeadBlockHash(JsonRpcRequest request)
    {
        if (request.Params?[0] is JObject forkChoiceState &&
            forkChoiceState["headBlockHash"] is not null)
        {
            return forkChoiceState["headBlockHash"]?.ToString() ?? string.Empty;
        }

        return string.Empty;
    }

    /// <summary>
    /// Extracts the parent beacon block root from the FCU request if available
    /// </summary>
    private static string? ExtractParentBeaconBlockRoot(JsonRpcRequest request)
    {
        // If the FCU request has payload attributes (param index 1)
        if (request.Params?.Count > 1 &&
            request.Params[1] is JObject payloadAttributes &&
            payloadAttributes["parentBeaconBlockRoot"] is not null)
        {
            string? parentBeaconBlockRoot = payloadAttributes["parentBeaconBlockRoot"]?.ToString();
            return !string.IsNullOrEmpty(parentBeaconBlockRoot) ? parentBeaconBlockRoot : null;
        }

        return null;
    }

    private void CleanupExpiredCacheEntries(object? state)
    {
        try
        {
            int removedCount = 0;
            var now = DateTime.UtcNow;

            // Find all expired entries
            var expiredKeys = _lhCacheTimestamps
                .Where(kvp => now - kvp.Value > _lhCacheExpiryTime)
                .Select(kvp => kvp.Key)
                .ToList();

            // Remove expired entries
            foreach (var key in expiredKeys)
            {
                if (_lhResponseCache.TryRemove(key, out _))
                {
                    removedCount++;
                }

                _lhCacheTimestamps.TryRemove(key, out _);
            }

            if (removedCount > 0)
            {
                _logger.Debug($"LH mode: Cleaned up {removedCount} expired cache entries. Cache size: {_lhResponseCache.Count}");
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Error during cache cleanup: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Safe substring helper that handles null or short strings
    /// </summary>
    private static string GetSafeSubstring(string input, int startIndex, int length)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        if (startIndex >= input.Length)
            return string.Empty;

        // Calculate safe length to prevent going past end of string
        int safeLength = Math.Min(length, input.Length - startIndex);
        return input.Substring(startIndex, safeLength);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            // Dispose managed resources
            _cacheCleanupTimer.Dispose();

            // Clear caches
            _lhResponseCache.Clear();
            _lhCacheTimestamps.Clear();
        }

        _disposed = true;
    }

    ~ForkChoiceUpdatedHandler()
    {
        Dispose(false);
    }
}
