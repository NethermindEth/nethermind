// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.EngineApiProxy.Config;
using Nethermind.EngineApiProxy.Models;
using Nethermind.EngineApiProxy.Services;
using Nethermind.EngineApiProxy.Utilities;
using Nethermind.Logging;

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
        _logger = logManager?.GetClassLogger<ForkChoiceUpdatedHandler>() ?? throw new ArgumentNullException(nameof(logManager));
        _requestForwarder = requestForwarder ?? throw new ArgumentNullException(nameof(requestForwarder));
        _messageQueue = messageQueue ?? throw new ArgumentNullException(nameof(messageQueue));
        _payloadTracker = payloadTracker ?? throw new ArgumentNullException(nameof(payloadTracker));
        _requestOrchestrator = requestOrchestrator ?? throw new ArgumentNullException(nameof(requestOrchestrator));

        // Start cache cleanup timer (every 30 seconds)
        _cacheCleanupTimer = new System.Threading.Timer(CleanupExpiredCacheEntries, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public async Task<JsonRpcResponse> HandleRequest(JsonRpcRequest request) => _config.ValidationMode switch
    {
        ValidationMode.NewPayload => await HandleNewPayloadMode(request),
        ValidationMode.Merged => await HandleMergedMode(request),
        ValidationMode.Lighthouse => await HandleLighthouseMode(request),
        ValidationMode.ForkChoiceUpdated => await HandleForkChoiceUpdatedMode(request),
        _ => await HandleForkChoiceUpdatedMode(request)
    };

    private async Task<JsonRpcResponse> HandleNewPayloadMode(JsonRpcRequest request)
    {
        _logger.Debug($"Processing engine_forkchoiceUpdated (NewPayload flow): {request}");
        return await _requestForwarder.ForwardRequestToExecutionClient(request);
    }

    private async Task<JsonRpcResponse> HandleMergedMode(JsonRpcRequest request)
    {
        _logger.Debug($"Processing engine_forkchoiceUpdated (Merged flow): {request}");

        try
        {
            bool shouldValidate = ShouldValidateBlock(request);
            _logger.Info($"---------------(Merged flow - FCU processing - Validation: {shouldValidate})-----------------");

            return await ProcessMergedFCU(request);
        }
        catch (Exception ex)
        {
            _logger.Error($"Error handling forkChoiceUpdated in Merged mode: {ex.Message}", ex);
            return JsonRpcResponse.CreateErrorResponse(request.Id, JsonRpcResponse.InternalErrorCode, $"Proxy error handling forkChoiceUpdated: {ex.Message}");
        }
    }

    private async Task<JsonRpcResponse> HandleLighthouseMode(JsonRpcRequest request)
    {
        _logger.Debug($"Processing engine_forkchoiceUpdated (Lighthouse flow): {request}");

        try
        {
            bool shouldValidate = ShouldValidateBlock(request);
            _logger.Info($"---------------(Lighthouse flow - FCU processing - Validation: {shouldValidate})-----------------");

            return await ProcessLHFCU(request);
        }
        catch (Exception ex)
        {
            _logger.Error($"Error handling forkChoiceUpdated in Lighthouse mode: {ex.Message}", ex);
            return JsonRpcResponse.CreateErrorResponse(request.Id, JsonRpcResponse.InternalErrorCode, $"Proxy error handling forkChoiceUpdated: {ex.Message}");
        }
    }

    private async Task<JsonRpcResponse> HandleForkChoiceUpdatedMode(JsonRpcRequest request)
    {
        _logger.Debug($"Processing engine_forkchoiceUpdated (ForkChoiceUpdated flow): {request}");

        try
        {
            bool shouldValidate = ShouldValidateBlock(request);
            _logger.Info($"---------------(ForkChoiceUpdated flow - FCU processing - Validation: {shouldValidate})-----------------");

            if (shouldValidate)
            {
                return await ProcessWithValidation(request);
            }

            return await ProcessWithoutValidation(request);
        }
        catch (Exception ex)
        {
            _logger.Error($"Error handling forkChoiceUpdated: {ex.Message}", ex);
            return JsonRpcResponse.CreateErrorResponse(request.Id, JsonRpcResponse.InternalErrorCode, $"Proxy error handling forkChoiceUpdated: {ex.Message}");
        }
    }

    private bool ShouldValidateBlock(JsonRpcRequest request)
    {
        if (!_config.ValidateAllBlocks)
        {
            return false;
        }

        if (request.Params is null || request.Params.Count == 0)
        {
            return false;
        }

        // Check if payload attributes (second parameter) is present and is an actual object
        bool hasPayloadAttributes = HasPayloadAttributes(request);

        _logger.Debug($"ValidateAllBlocks is enabled, params count: {request.Params.Count}, hasPayloadAttributes: {hasPayloadAttributes}");

        if (hasPayloadAttributes)
        {
            _logger.Debug("Skipping validation because request already contains payload attributes");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Checks if the request contains non-null payload attributes (second parameter).
    /// </summary>
    private static bool HasPayloadAttributes(JsonRpcRequest request)
        => request.Params is { Count: > 1 } && request.Params[1] is JsonObject;

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
                if (ex.Message.Contains("is not supported") || ex.Message.Contains("is not implemented"))
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
        JsonRpcResponse response = await _requestForwarder.ForwardRequestToExecutionClient(request);

        // If response contains payloadId, store it for tracking
        if (response.Result is JsonObject resultObj &&
            resultObj["payloadId"] is not null &&
            request.Params is not null &&
            request.Params.Count > 0 &&
            request.Params[0] is JsonObject fcState &&
            fcState["headBlockHash"] is not null)
        {
            string payloadId = resultObj["payloadId"]?.ToString() ?? string.Empty;
            string headBlockHashStr = fcState["headBlockHash"]?.ToString() ?? string.Empty;

            if (!string.IsNullOrEmpty(payloadId) && !string.IsNullOrEmpty(headBlockHashStr))
            {
                Hash256 headBlockHash = new(Bytes.FromHexString(headBlockHashStr));
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
                Hash256 headBlockHash = new(Bytes.FromHexString(headBlockHashStr));
                _payloadTracker.TrackPayload(headBlockHash, payloadId);
            }

            // 2. Create a response without payloadId to return to CL
            JsonRpcResponse response = new()
            {
                Id = request.Id,
                Result = new JsonObject
                {
                    ["payloadStatus"] = new JsonObject
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
            return JsonRpcResponse.CreateErrorResponse(request.Id, JsonRpcResponse.InternalErrorCode, $"Proxy error handling merged FCU: {ex.Message}");
        }
    }

    private async Task<JsonRpcResponse> ProcessLHFCU(JsonRpcRequest request)
    {
        _logger.Debug("Processing LH FCU flow");

        try
        {
            string headBlockHashStr = ExtractHeadBlockHash(request);

            if (HasPayloadAttributes(request))
            {
                _logger.Info("LH mode: got FCU request with payload attributes, processing validation flow");
                string requestFingerprint = ComputeRequestFingerprint(request);
                string fingerprintShort = requestFingerprint[..Math.Min(10, requestFingerprint.Length)];

                // Check if we've seen this exact request recently
                if (_lhResponseCache.TryGetValue(requestFingerprint, out JsonRpcResponse? cachedResponse) &&
                    _lhCacheTimestamps.TryGetValue(requestFingerprint, out DateTime timestamp))
                {
                    if (DateTime.UtcNow - timestamp < _lhCacheExpiryTime)
                    {
                        _logger.Info($"LH mode: Duplicate FCU request detected (fingerprint: {fingerprintShort}...), returning cached response to CL");
                        return CloneResponseWithNewId(cachedResponse, request.Id);
                    }

                    _logger.Debug($"LH mode: Cache entry expired for fingerprint: {fingerprintShort}..., will forward to EL");
                    _lhResponseCache.TryRemove(requestFingerprint, out _);
                    _lhCacheTimestamps.TryRemove(requestFingerprint, out _);
                }

                _logger.Info($"LH mode: New unique FCU request with payload attributes (fingerprint: {fingerprintShort}...)");

                // Pre-emptively store parentBeaconBlockRoot before we have a payloadId
                string? extractedParentBeaconBlockRoot = ExtractParentBeaconBlockRoot(request);
                if (!string.IsNullOrEmpty(headBlockHashStr) && !string.IsNullOrEmpty(extractedParentBeaconBlockRoot))
                {
                    try
                    {
                        Hash256 headBlockHash = new(Bytes.FromHexString(headBlockHashStr));
                        _payloadTracker.AssociateParentBeaconBlockRoot(headBlockHash, extractedParentBeaconBlockRoot);
                        _logger.Debug($"Pre-emptively stored parentBeaconBlockRoot {extractedParentBeaconBlockRoot} for head block {headBlockHash}");
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"Failed to pre-emptively store parentBeaconBlockRoot: {ex.Message}");
                    }
                }

                JsonRpcResponse response = await _requestForwarder.ForwardRequestToExecutionClient(request);

                if (TrackPayloadIdFromResponse(response, request, headBlockHashStr, expectPayloadId: true))
                {
                    if (_lhResponseCache.Count < 100)
                    {
                        _lhResponseCache[requestFingerprint] = response;
                        _lhCacheTimestamps[requestFingerprint] = DateTime.UtcNow;
                        _logger.Debug($"LH mode: Cached response for FCU request (fingerprint: {fingerprintShort}...)");
                    }
                    else
                    {
                        _logger.Warn($"LH mode: Cache full (size: {_lhResponseCache.Count}), not caching response");
                    }
                }

                return response;
            }

            // For requests without payload attributes, just forward as usual
            _logger.Info("LH mode: FCU request without payload attributes, forwarding normally. This usually means the node is not synced yet.");

            JsonRpcResponse plainResponse = await _requestForwarder.ForwardRequestToExecutionClient(request);
            TrackPayloadIdFromResponse(plainResponse, request, headBlockHashStr, expectPayloadId: false);
            return plainResponse;
        }
        catch (Exception ex)
        {
            _logger.Error($"Error handling LH FCU: {ex.Message}", ex);
            return JsonRpcResponse.CreateErrorResponse(request.Id, JsonRpcResponse.InternalErrorCode, $"Proxy error handling LH FCU: {ex.Message}");
        }
    }

    /// <summary>
    /// If <paramref name="response"/> carries a payloadId, records it in the payload tracker
    /// (along with the parentBeaconBlockRoot from <paramref name="request"/>, when present).
    /// When <paramref name="expectPayloadId"/> is false a missing payloadId is normal (e.g. an
    /// FCU without payload attributes), so we log at Debug instead of Warn.
    /// Returns true if a payloadId was successfully tracked.
    /// </summary>
    private bool TrackPayloadIdFromResponse(JsonRpcResponse response, JsonRpcRequest request, string headBlockHashStr, bool expectPayloadId)
    {
        if (response.Result is not JsonObject resultObj || resultObj["payloadId"] is null)
        {
            if (expectPayloadId)
            {
                _logger.Warn("LH validation flow received response with no payloadId");
            }
            else
            {
                _logger.Debug("LH FCU response without payloadId (expected for non-building slots)");
            }
            return false;
        }

        string payloadId = resultObj["payloadId"]?.ToString() ?? string.Empty;
        if (string.IsNullOrEmpty(payloadId) || string.IsNullOrEmpty(headBlockHashStr))
        {
            if (expectPayloadId)
            {
                _logger.Warn("LH validation flow received response but payloadId or headBlockHash is empty");
            }
            else
            {
                _logger.Debug("LH FCU response had empty payloadId or headBlockHash");
            }
            return false;
        }

        Hash256 headBlockHash = new(Bytes.FromHexString(headBlockHashStr));
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
        return true;
    }

    /// <summary>
    /// Computes a fingerprint for FCU requests to identify duplicates
    /// </summary>
    private static string ComputeRequestFingerprint(JsonRpcRequest request) => request.Params?.ToJsonString() ?? string.Empty;

    /// <summary>
    /// Creates a clone of a response with a new ID to match the current request
    /// </summary>
    private static JsonRpcResponse CloneResponseWithNewId(JsonRpcResponse response, JsonNode? newId) => new()
    {
        Id = newId?.DeepClone(),
        Result = response.Result?.DeepClone(),
        Error = response.Error is not null
            ? new JsonRpcError
            {
                Code = response.Error.Code,
                Message = response.Error.Message,
                Data = response.Error.Data?.DeepClone()
            }
            : null
    };

    private static string ExtractHeadBlockHash(JsonRpcRequest request)
    {
        if (request.Params is { Count: > 0 } && request.Params[0] is JsonObject forkChoiceState &&
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
            request.Params[1] is JsonObject payloadAttributes &&
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
            DateTime now = DateTime.UtcNow;

            // Find and remove expired entries
            List<string> expiredKeys = [];
            foreach (KeyValuePair<string, DateTime> kvp in _lhCacheTimestamps)
            {
                if (now - kvp.Value > _lhCacheExpiryTime)
                {
                    expiredKeys.Add(kvp.Key);
                }
            }

            foreach (string key in expiredKeys)
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cacheCleanupTimer.Dispose();
        _lhResponseCache.Clear();
        _lhCacheTimestamps.Clear();
    }
}
