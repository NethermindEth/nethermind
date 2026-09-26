// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.EngineApiProxy.Config;
using Nethermind.EngineApiProxy.Models;
using Nethermind.EngineApiProxy.Utilities;
using Nethermind.Logging;

namespace Nethermind.EngineApiProxy.Services;

/// <summary>
/// Orchestrates the flow of requests for block validation
/// </summary>
public class RequestOrchestrator(
    HttpClient httpClient,
    BlockDataFetcher blockFetcher,
    PayloadAttributesGenerator attributesGenerator,
    MessageQueue messageQueue,
    PayloadTracker payloadTracker,
    ProxyConfig config,
    ILogManager logManager)
{
    private readonly BlockDataFetcher _blockFetcher = blockFetcher ?? throw new ArgumentNullException(nameof(blockFetcher));
    private readonly PayloadAttributesGenerator _attributesGenerator = attributesGenerator ?? throw new ArgumentNullException(nameof(attributesGenerator));
    private readonly MessageQueue _messageQueue = messageQueue ?? throw new ArgumentNullException(nameof(messageQueue));
    private readonly PayloadTracker _payloadTracker = payloadTracker ?? throw new ArgumentNullException(nameof(payloadTracker));
    private readonly ILogger _logger = logManager?.GetClassLogger<RequestOrchestrator>() ?? throw new ArgumentNullException(nameof(logManager));
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly ProxyConfig _config = config ?? throw new ArgumentNullException(nameof(config));
    private readonly BlobHashComputer _blobHashComputer = new(logManager);

    /// <summary>
    /// Handles the fork choice updated validation flow
    /// </summary>
    /// <param name="originalRequest">The original FCU request</param>
    /// <param name="headBlockHash">The head block hash from the FCU request</param>
    /// <returns>The payloadId from the FCU response</returns>
    public async Task<string> HandleFCUWithValidation(JsonRpcRequest originalRequest, string headBlockHash)
    {
        _logger.Debug($"Starting validation flow for head block: {headBlockHash}");

        // First get the payload ID
        string payloadId = await GetPayloadID(originalRequest, headBlockHash);

        if (!string.IsNullOrEmpty(payloadId))
        {
            // Then perform validation if we got a valid payload ID
            await DoValidationForFCU(payloadId, headBlockHash, originalHeaders: originalRequest.OriginalHeaders);
        }

        return payloadId;
    }

    /// <summary>
    /// Gets a payload ID by sending a FCU request with payload attributes
    /// </summary>
    /// <param name="originalRequest">The original FCU request</param>
    /// <param name="headBlockHash">The head block hash from the FCU request</param>
    /// <returns>The payloadId from the FCU response</returns>
    public async Task<string> GetPayloadID(JsonRpcRequest originalRequest, string headBlockHash, bool fromCL = false)
    {
        _logger.Debug($"Getting payload ID for head block: {headBlockHash}");
        string targetHost = _httpClient.BaseAddress?.ToString() ?? "unknown";
        _logger.Debug($"Will use execution client at: {targetHost}");

        try
        {
            // For Lighthouse mode, we don't need to do any special processing here as the ForkChoiceUpdatedHandler
            // will directly forward the original request and extract the payloadId from the response
            if (_config.ValidationMode == ValidationMode.Lighthouse)
            {
                _logger.Info("Lighthouse mode: Skipping GetPayloadID processing as we're using the original request/response flow");
                return string.Empty;
            }

            JsonObject payloadAttributes;

            // Original behavior for non-LH modes
            if (!fromCL)
            {
                JsonObject? blockData = await _blockFetcher.GetBlockByHash(headBlockHash, originalRequest.OriginalHeaders);
                if (blockData is null)
                {
                    _logger.Warn($"Failed to fetch block data for hash: {headBlockHash}");
                    return string.Empty;
                }

                // Generate payload attributes
                payloadAttributes = _attributesGenerator.GeneratePayloadAttributes(blockData);
            }
            else
            {
                // Get the head block data
                JsonObject? beaconBlockHeader = await _blockFetcher.GetBeaconBlockHeader();
                if (beaconBlockHeader is null)
                {
                    _logger.Warn("Failed to fetch beacon block header");
                    return string.Empty;
                }
                headBlockHash = beaconBlockHeader["data"]?["root"]?.ToString() ?? string.Empty;

                JsonObject? blockData = await _blockFetcher.GetBeaconBlock(headBlockHash);
                if (blockData is null)
                {
                    _logger.Warn($"Failed to fetch block data for hash: {headBlockHash}");
                    return string.Empty;
                }

                if ((blockData["data"]?["message"]?["body"]?["execution_payload"])?.DeepClone() is not JsonObject payload)
                {
                    _logger.Warn($"Failed to fetch payload for hash: {headBlockHash}");
                    return string.Empty;
                }
                blockData["timestamp"] = payload["timestamp"]?.ToString();
                blockData["prevRandao"] = payload["prev_randao"]?.ToString();
                blockData["parentBeaconBlockRoot"] = headBlockHash;
                blockData["withdrawals"] = payload["withdrawals"]?.DeepClone() ?? new JsonArray();

                payloadAttributes = _attributesGenerator.GeneratePayloadAttributes(blockData);
            }

            // 3. Clone the request and add payload attributes
            // (CloneRequest already deep-clones OriginalHeaders so the modified request has its own copy.)
            JsonRpcRequest modifiedRequest = CloneRequest(originalRequest);
            modifiedRequest.Params ??= [];

            // Ensure the first parameter (fork choice state) exists
            if (modifiedRequest.Params.Count == 0)
            {
                modifiedRequest.Params.Add(new JsonObject
                {
                    ["headBlockHash"] = headBlockHash,
                    ["finalizedBlockHash"] = headBlockHash, // Use same hash as finalized for simplicity
                    ["safeBlockHash"] = headBlockHash // Use same hash as safe for simplicity
                });
            }

            // Add or replace payload attributes
            if (modifiedRequest.Params.Count == 1)
            {
                modifiedRequest.Params.Add(payloadAttributes);
            }
            else
            {
                modifiedRequest.Params[1] = payloadAttributes;
            }

            // 4. Send modified FCU to execution client
            _logger.Debug($"Sending modified FCU with payload attributes: {modifiedRequest}");
            JsonRpcResponse fcuResponse = await SendJsonRpcRequest(modifiedRequest);

            // 5. Extract payload ID from response
            if (fcuResponse.Result is JsonObject resultObj && resultObj["payloadId"] is not null)
            {
                string payloadId = resultObj["payloadId"]?.ToString() ?? string.Empty;

                if (string.IsNullOrEmpty(payloadId))
                {
                    string payloadStatus = resultObj["payloadStatus"]?["status"]?.ToString() ?? string.Empty;
                    if (payloadStatus == "SYNCING")
                    {
                        _logger.Warn($"Payload is SYNCING, skipping validation for payloadId: {payloadId}");
                        return string.Empty;
                    }
                    else if (payloadStatus == "VALID")
                    {
                        _logger.Warn($"Payload is VALID, but payloadId is null. FCU may be already sent to EL");
                        return string.Empty;
                    }
                    throw new InvalidOperationException($"Received empty payloadId from FCU response: {resultObj}");
                }

                _logger.Info($"Received payloadId: {payloadId} for head block: {headBlockHash}");
                return payloadId;
            }
            else
            {
                throw new InvalidOperationException("FCU response did not contain a payloadId");
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Error in getting payload ID: {ex.Message}", ex);
            throw;
        }
    }

    /// <summary>
    /// Performs validation for a payload ID
    /// </summary>
    /// <param name="payloadId">The payload ID to validate</param>
    /// <param name="parentBeaconBlockRoot">Optional parent beacon block root</param>
    /// <returns>True if validation succeeded</returns>
    public async Task<bool> DoValidationForFCU(
        string payloadId,
        string parentBeaconBlockRoot,
        bool isApproval = false,
        IReadOnlyDictionary<string, string>? originalHeaders = null)
    {
        _logger.Debug($"Starting validation process for payloadId {payloadId}, parentBeaconBlockRoot: {parentBeaconBlockRoot}");

        try
        {
            // Get payload ID from the tracking system
            Hash256? headBlock = _payloadTracker.GetHeadBlock(payloadId);
            if (headBlock is null)
            {
                _logger.Error($"No head block found for payloadId {payloadId}");
                return false;
            }

            // Wait a short time for EL to prepare the payload
            await Task.Delay(500);

            // Get the payload and process it, passing along the parentBeaconBlockRoot
            JsonRpcResponse response = await GetAndProcessPayload(payloadId, headBlock, parentBeaconBlockRoot, originalHeaders);

            // Check if the response indicates success
            if (response.Error is not null)
            {
                _logger.Error($"Error in payload validation: {response.Error.Code} - {response.Error.Message}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            // If the configured getPayload method fails, log it but don't throw an exception
            string notSupportedMessage = $"The method '{_config.GetPayloadMethod}' is not supported";
            if (ex.Message.Contains(notSupportedMessage) ||
                (ex.InnerException is not null && ex.InnerException.Message.Contains(notSupportedMessage)))
            {
                _logger.Warn($"Execution client does not support {_config.GetPayloadMethod}. Skipping payload validation for payloadId: {payloadId}");
                return false;
            }

            _logger.Error($"Error in payload validation: {ex.Message}", ex);
            return false;
        }
    }

    /// <summary>
    /// Gets the payload for a given payload ID and processes it
    /// </summary>
    /// <param name="payloadId">The payload ID to get</param>
    /// <param name="parentBeaconBlockRoot">The parent beacon block root from the block data</param>
    /// <returns>The get payload response</returns>
    private async Task<JsonRpcResponse> GetAndProcessPayload(
        string payloadId,
        Hash256 headBlock,
        string? parentBeaconBlockRoot = null,
        IReadOnlyDictionary<string, string>? originalHeaders = null)
    {
        _logger.Info($"Getting payload for payloadId {payloadId}");

        try
        {
            string targetHost = _httpClient.BaseAddress?.ToString() ?? "unknown";
            _logger.Debug($"Getting payload from execution client at: {targetHost}");

            // Log the parentBeaconBlockRoot to track it through the validation flow
            if (!string.IsNullOrEmpty(parentBeaconBlockRoot))
            {
                _logger.Debug($"Using parentBeaconBlockRoot in validation flow: {parentBeaconBlockRoot}");
            }
            else
            {
                _logger.Debug("No parentBeaconBlockRoot provided for validation flow");
            }

            // Create getPayload request
            JsonRpcRequest getPayloadRequest = new(
                _config.GetPayloadMethod,
                new JsonArray(payloadId),
                Guid.NewGuid().ToString())
            {
                OriginalHeaders = CopyAuthHeader(originalHeaders)
            };

            // Send request to get the payload
            JsonRpcResponse payloadResponse = await SendJsonRpcRequest(getPayloadRequest);

            // Check for error in response
            if (payloadResponse.Error is not null)
            {
                // Check for unsupported method error
                if (payloadResponse.Error.Code == -32601 ||
                    (payloadResponse.Error.Message?.Contains("not supported") == true))
                {
                    throw new InvalidOperationException($"Method {_config.GetPayloadMethod} is not supported: {payloadResponse.Error.Message}");
                }

                throw new InvalidOperationException($"Error getting payload: {payloadResponse.Error.Code} - {payloadResponse.Error.Message}");
            }

            if (payloadResponse.Result is JsonObject payload)
            {
                try
                {
                    // Extract blobsBundle and compute versioned hashes
                    JsonObject? blobsBundle = payload["blobsBundle"] as JsonObject;
                    JsonArray blobVersionedHashes = _blobHashComputer.ComputeVersionedHashes(blobsBundle);

                    // Store blob versioned hashes in tracker for this head block
                    if (blobVersionedHashes.Count > 0)
                    {
                        string[] hashArray = BlobHashComputer.ToStringArray(blobVersionedHashes);
                        _payloadTracker.AssociateBlobVersionedHashes(headBlock, hashArray);
                        _logger.Debug($"Stored {hashArray.Length} blobVersionedHashes for head block {headBlock}");
                    }

                    // Create newPayload request from the payload
                    _logger.Info($"Creating newPayload request from payload with parentBeaconBlockRoot: {parentBeaconBlockRoot}");
                    JsonRpcRequest newPayloadRequest = CreateNewPayloadRequest(payload, parentBeaconBlockRoot, blobVersionedHashes);
                    newPayloadRequest.OriginalHeaders = CopyAuthHeader(originalHeaders);

                    // Send newPayload request
                    _logger.Debug($"Sending synthetic newPayload request: {newPayloadRequest}");
                    JsonRpcResponse newPayloadResponse = await SendJsonRpcRequest(newPayloadRequest);

                    // Check if newPayload resulted in an error
                    if (newPayloadResponse.Error is not null)
                    {
                        _logger.Warn($"Synthetic newPayload validation resulted in error: {newPayloadResponse.Error.Code} - {newPayloadResponse.Error.Message}");
                    }
                    else if (newPayloadResponse.Result is JsonObject resultObj && resultObj["status"]?.ToString() == "INVALID")
                    {
                        _logger.Warn($"Synthetic newPayload validation returned status INVALID: {resultObj["validationError"]}");
                    }
                    else
                    {
                        _logger.Debug($"Completed synthetic block validation flow for payloadId: {payloadId}");
                    }

                    return newPayloadResponse;
                }
                catch (Exception ex)
                {
                    // If newPayload fails, just log the error but don't stop the process
                    _logger.Warn($"Error in synthetic newPayload validation, continuing: {ex.Message}");
                    return JsonRpcResponse.CreateErrorResponse(payloadResponse.Id, JsonRpcResponse.InternalErrorCode, $"Error in synthetic newPayload validation: {ex.Message}");
                }
            }
            else
            {
                throw new InvalidOperationException($"Invalid payload response format for payloadId: {payloadId}");
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Error getting and processing payload: {ex.Message}", ex);
            throw;
        }
    }

    /// <summary>
    /// Creates a newPayload request from a getPayload response
    /// </summary>
    /// <param name="payload">The payload from getPayload</param>
    /// <param name="parentBeaconBlockRoot">The parent beacon block root extracted from block data</param>
    /// <param name="blobVersionedHashes">The blob versioned hashes computed from blobsBundle</param>
    /// <returns>A newPayload request</returns>
    private JsonRpcRequest CreateNewPayloadRequest(JsonObject payload, string? parentBeaconBlockRoot = null, JsonArray? blobVersionedHashes = null)
    {
        try
        {
            // Extract the executionPayload from the response
            JsonNode executionPayload = (payload["executionPayload"] ?? payload).DeepClone();

            // EIP-7685 executionRequests (getPayloadV4+). The block's requestsHash is
            // derived from this list, so replaying the payload without it makes the EL
            // recompute a different block hash and reject the block as invalid.
            JsonNode executionRequests = payload["executionRequests"]?.DeepClone() ?? new JsonArray();

            // Use provided blobVersionedHashes or empty array
            blobVersionedHashes ??= [];
            _logger.Debug($"CreateNewPayloadRequest: Including {blobVersionedHashes.Count} blobVersionedHashes in synthetic newPayload");

            // Check if parentBeaconBlockRoot is provided
            if (string.IsNullOrEmpty(parentBeaconBlockRoot))
            {
                // Try to get the parentBeaconBlockRoot from the PayloadTracker
                string? blockHash = executionPayload["parentHash"]?.ToString();

                if (!string.IsNullOrEmpty(blockHash))
                {
                    try
                    {
                        // Try to get the parentBeaconBlockRoot from the PayloadTracker
                        Hash256 hash = new(Bytes.FromHexString(blockHash));

                        // First try the fast-path TryGet method to avoid multiple lookups
                        if (_payloadTracker.TryGetParentBeaconBlockRoot(hash, out string? trackedParentBeaconBlockRoot) &&
                            !string.IsNullOrEmpty(trackedParentBeaconBlockRoot))
                        {
                            parentBeaconBlockRoot = trackedParentBeaconBlockRoot;
                            _logger.Info($"CreateNewPayloadRequest: Using parentBeaconBlockRoot {parentBeaconBlockRoot} from payload tracker for parent hash {blockHash}");
                        }
                        else
                        {
                            _logger.Warn($"No parentBeaconBlockRoot found in tracker for block with hash {blockHash}. This could lead to validation issues.");
                            parentBeaconBlockRoot = null;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"Error retrieving parentBeaconBlockRoot for block hash {blockHash}: {ex.Message}");
                        parentBeaconBlockRoot = null;
                    }
                }
                else
                {
                    _logger.Warn("Missing parentHash in payload. Cannot retrieve parentBeaconBlockRoot from tracker.");
                    parentBeaconBlockRoot = null;
                }
            }
            else
            {
                _logger.Debug($"Using provided parentBeaconBlockRoot in {_config.NewPayloadMethod}: {parentBeaconBlockRoot}");
            }

            // Create the parameter array for the newPayload request
            JsonArray parameters =
            [
                executionPayload,          // First parameter: executionPayload
                blobVersionedHashes,       // Second parameter: blobVersionedHashes
                parentBeaconBlockRoot,     // Third parameter: parentBeaconBlockRoot
                executionRequests          // Fourth parameter: executionRequests (EIP-7685)
            ];

            _logger.Debug($"Created {_config.NewPayloadMethod} request with {blobVersionedHashes.Count} blobVersionedHashes, {(executionRequests as JsonArray)?.Count ?? 0} executionRequests, parentBeaconBlockRoot: {parentBeaconBlockRoot}");

            return new JsonRpcRequest(
                _config.NewPayloadMethod,
                parameters,
                Guid.NewGuid().ToString()
            );
        }
        catch (Exception ex)
        {
            _logger.Error($"Error creating newPayload request: {ex.Message}", ex);
            throw;
        }
    }

    /// <summary>
    /// Sends a JSON-RPC request to the execution client
    /// </summary>
    /// <param name="request">The request to send</param>
    /// <returns>The response from the execution client</returns>
    protected virtual async Task<JsonRpcResponse> SendJsonRpcRequest(JsonRpcRequest request)
    {
        try
        {
            // Serialize request
            string requestJson = JsonSerializer.Serialize(request);
            string targetHost = _httpClient.BaseAddress?.ToString() ?? "unknown";
            _logger.Debug($"Forwarding validation request to EL at: {targetHost}");
            _logger.Info($"PR -> EL|{request.Method}|V|{requestJson}");
            StringContent httpContent = new(requestJson, Encoding.UTF8, "application/json");

            // Create a request message instead of using PostAsync
            HttpRequestMessage requestMessage = new(HttpMethod.Post, "")
            {
                Content = httpContent
            };

            bool authHeaderAdded = HttpHeaderForwarder.AttachForwardedHeaders(requestMessage, request.OriginalHeaders);
            if (!authHeaderAdded)
            {
                _logger.Warn("No Authorization header found in request.OriginalHeaders");
            }

            // Send request
            _logger.Debug($"Sending request with method: {request.Method}, HasAuth: {authHeaderAdded}, Target: {targetHost}");
            HttpResponseMessage response = await _httpClient.SendAsync(requestMessage);
            if (!response.IsSuccessStatusCode)
            {
                string errorContent = await response.Content.ReadAsStringAsync();
                _logger.Error($"HTTP request failed with status code: {response.StatusCode}, Content: {errorContent}");

                // Return an error response instead of throwing in tests
                return JsonRpcResponse.CreateErrorResponse(request.Id, JsonRpcResponse.InternalErrorCode, $"Proxy error: HTTP error: {response.StatusCode}");
            }

            // Parse response
            string responseJson = await response.Content.ReadAsStringAsync();
            _logger.Debug($"Received response from EL at: {targetHost}");
            _logger.Info($"EL -> PR|{request.Method}|V|{responseJson}");

            try
            {
                JsonRpcResponse? jsonRpcResponse = JsonSerializer.Deserialize<JsonRpcResponse>(responseJson);
                if (jsonRpcResponse is null)
                {
                    _logger.Error($"Failed to deserialize JSON-RPC response: {responseJson}");

                    return JsonRpcResponse.CreateErrorResponse(request.Id, -32700, "Proxy error: Invalid JSON response");
                }

                // Check for JSON-RPC error
                if (jsonRpcResponse.Error is not null)
                {
                    _logger.Error($"JSON-RPC error: {jsonRpcResponse.Error.Code} - {jsonRpcResponse.Error.Message}");
                }

                return jsonRpcResponse;
            }
            catch (JsonException ex)
            {
                _logger.Error($"Failed to deserialize JSON-RPC response: {ex.Message}", ex);

                return JsonRpcResponse.CreateErrorResponse(request.Id, -32700, "Proxy error: Invalid JSON response");
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Error sending JSON-RPC request: {ex.Message}", ex);

            // Return an error response instead of throwing
            return JsonRpcResponse.CreateErrorResponse(request.Id, JsonRpcResponse.InternalErrorCode, $"Proxy error: Sending JSON-RPC request: {ex.Message}");
        }
    }

    /// <summary>
    /// Copies the Authorization header from the supplied headers into a new dictionary suitable for
    /// attaching as <see cref="JsonRpcRequest.OriginalHeaders"/> on a synthetic request. Returns null
    /// if no Authorization header is present.
    /// </summary>
    private static Dictionary<string, string>? CopyAuthHeader(IReadOnlyDictionary<string, string>? source)
    {
        if (source is null) return null;
        if (!source.TryGetValue("Authorization", out string? authHeader) || string.IsNullOrEmpty(authHeader))
        {
            return null;
        }
        return new Dictionary<string, string> { ["Authorization"] = authHeader };
    }

    /// <summary>
    /// Clones a JSON-RPC request
    /// </summary>
    /// <param name="request">The request to clone</param>
    /// <returns>A clone of the request</returns>
    private static JsonRpcRequest CloneRequest(JsonRpcRequest request) => new(
        request.Method,
        request.Params?.DeepClone() as JsonArray,
        request.Id?.DeepClone())
    {
        OriginalHeaders = request.OriginalHeaders is not null
            ? new Dictionary<string, string>(request.OriginalHeaders)
            : null
    };

    /// <summary>
    /// Checks if the execution payload for the given payloadId matches the expected block hash
    /// and performs validation if appropriate
    /// </summary>
    /// <param name="payloadId">The payload ID to check</param>
    /// <param name="expectedBlockHash">The expected block hash from the original request</param>
    /// <returns>True if validation should proceed, false if there's a mismatch</returns>
    public async Task<bool> ValidatePayloadWithBlockHashCheck(string payloadId, string expectedBlockHash)
    {
        _logger.Debug($"Checking payload {payloadId} against expected block hash {expectedBlockHash}");

        try
        {
            // Get the head block hash from the payload tracker
            Hash256? headBlock = _payloadTracker.GetHeadBlock(payloadId);
            if (headBlock is null)
            {
                _logger.Error($"No head block found for payloadId {payloadId}");
                return false;
            }

            // Get the payload using the existing method
            JsonRpcResponse payloadResponse = await GetAndProcessPayload(payloadId, headBlock);

            // Extract the block hash from the response
            if (payloadResponse.Result is JsonObject payload)
            {
                JsonObject? executionPayload = payload["executionPayload"] as JsonObject;
                if (executionPayload is not null)
                {
                    string? synthBlockHash = executionPayload["blockHash"]?.ToString();
                    if (!string.IsNullOrEmpty(synthBlockHash) && !string.IsNullOrEmpty(expectedBlockHash) && synthBlockHash != expectedBlockHash)
                    {
                        _logger.Info($"LH mode: Payload from EL has different block hash ({synthBlockHash}) than CL block ({expectedBlockHash})");
                        _logger.Info($"LH mode: Skipping validation to avoid potential block hash mismatch");
                        return false;
                    }
                }
            }

            // If we got here, then either there was no mismatch or we couldn't determine
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warn($"Error checking payload against block hash: {ex.Message}");
            return false;
        }
    }
}
