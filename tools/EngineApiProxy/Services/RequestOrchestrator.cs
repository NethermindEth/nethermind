// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.EngineApiProxy.Config;
using Nethermind.EngineApiProxy.Models;
using Nethermind.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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
    private readonly ILogger _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly ProxyConfig _config = config ?? throw new ArgumentNullException(nameof(config));

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
            await DoValidationForFCU(payloadId, headBlockHash);
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
            // Check if Authorization header is present in original request
            if (originalRequest.OriginalHeaders != null &&
                originalRequest.OriginalHeaders.TryGetValue("Authorization", out var authHeader))
            {
                _logger.Debug($"Original request contains Authorization header: {authHeader.Substring(0, Math.Min(10, authHeader.Length))}...");
            }
            else
            {
                _logger.Debug("Original request does not contain Authorization header");
            }

            // Check if DefaultRequestHeaders has Authorization
            if (_httpClient.DefaultRequestHeaders.Contains("Authorization"))
            {
                var authHeaderValue = _httpClient.DefaultRequestHeaders.GetValues("Authorization").FirstOrDefault();
                _logger.Debug($"HttpClient DefaultRequestHeaders contains Authorization: {authHeaderValue?.Substring(0, Math.Min(10, authHeaderValue?.Length ?? 0))}...");
            }
            else
            {
                _logger.Debug("HttpClient DefaultRequestHeaders does not contain Authorization");
            }

            // For LH mode, we don't need to do any special processing here as the ForkChoiceUpdatedHandler
            // will directly forward the original request and extract the payloadId from the response
            if (_config.ValidationMode == ValidationMode.LH)
            {
                _logger.Info("LH mode: Skipping GetPayloadID processing as we're using the original request/response flow");
                return string.Empty;
            }

            JObject payloadAttributes = new JObject();

            // Original behavior for non-LH modes
            if (!fromCL)
            {
                var blockData = await _blockFetcher.GetBlockByHash(headBlockHash);
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
                var beaconBlockHeader = await _blockFetcher.GetBeaconBlockHeader();
                if (beaconBlockHeader is null)
                {
                    _logger.Warn("Failed to fetch beacon block header");
                    return string.Empty;
                }
                headBlockHash = beaconBlockHeader["data"]?["root"]?.ToString() ?? string.Empty;

                var blockData = await _blockFetcher.GetBeaconBlock(headBlockHash);
                if (blockData is null)
                {
                    _logger.Warn($"Failed to fetch block data for hash: {headBlockHash}");
                    return string.Empty;
                }
                else
                {
                    var payload = blockData["data"]?["message"]?["body"]?["execution_payload"]?.ToObject<JObject>();
                    if (payload is null)
                    {
                        _logger.Warn($"Failed to fetch payload for hash: {headBlockHash}");
                        return string.Empty;
                    }
                    blockData["timestamp"] = payload["timestamp"]?.ToString() ?? null;
                    blockData["prevRandao"] = payload["prev_randao"]?.ToString() ?? null;
                    blockData["parentBeaconBlockRoot"] = headBlockHash;
                    blockData["withdrawals"] = payload["withdrawals"] ?? new JArray();
                }
                payloadAttributes = _attributesGenerator.GeneratePayloadAttributes(blockData);
            }

            // 3. Clone the request and add payload attributes
            var modifiedRequest = CloneRequest(originalRequest);
            modifiedRequest.Params ??= [];

            // Make sure we explicitly copy the originalRequest headers to ensure auth is preserved
            modifiedRequest.OriginalHeaders = originalRequest.OriginalHeaders != null
                ? new Dictionary<string, string>(originalRequest.OriginalHeaders)
                : null;

            // Ensure the first parameter (fork choice state) exists
            if (modifiedRequest.Params.Count == 0)
            {
                modifiedRequest.Params.Add(new JObject
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
            var fcuResponse = await SendJsonRpcRequest(modifiedRequest);

            // 5. Extract payload ID from response
            if (fcuResponse.Result is JObject resultObj && resultObj["payloadId"] != null)
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
    public async Task<bool> DoValidationForFCU(string payloadId, string parentBeaconBlockRoot, bool isApproval = false)
    {
        _logger.Debug($"Starting validation process for payloadId {payloadId}, parentBeaconBlockRoot: {parentBeaconBlockRoot}");

        try
        {
            // Get payload ID from the tracking system
            var headBlock = _payloadTracker.GetHeadBlock(payloadId);
            if (headBlock is null)
            {
                _logger.Error($"No head block found for payloadId {payloadId}");
                return false;
            }

            // Wait a short time for EL to prepare the payload
            await Task.Delay(500);

            // Get the payload and process it, passing along the parentBeaconBlockRoot
            var response = await GetAndProcessPayload(payloadId, headBlock, parentBeaconBlockRoot);

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
            // If engine_getPayloadV4 fails, log it but don't throw an exception
            if (ex.ToString().Contains("The method 'engine_getPayloadV4' is not supported") ||
                (ex.InnerException != null && ex.InnerException.ToString().Contains("The method 'engine_getPayloadV4' is not supported")))
            {
                _logger.Warn($"Execution client does not support engine_getPayloadV4. Skipping payload validation for payloadId: {payloadId}");
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
    private async Task<JsonRpcResponse> GetAndProcessPayload(string payloadId, Hash256 headBlock, string? parentBeaconBlockRoot = null)
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
            var getPayloadRequest = new JsonRpcRequest(
                "engine_getPayloadV5",
                new JArray(payloadId),
                Guid.NewGuid().ToString());

            // Make sure any authorization headers from HttpClient are included
            if (_httpClient.DefaultRequestHeaders.Contains("Authorization"))
            {
                var authHeaderValue = _httpClient.DefaultRequestHeaders.GetValues("Authorization").FirstOrDefault();
                if (!string.IsNullOrEmpty(authHeaderValue))
                {
                    getPayloadRequest.OriginalHeaders = new Dictionary<string, string>
                    {
                        { "Authorization", authHeaderValue }
                    };
                    _logger.Debug("Added Authorization header to getPayload request from HttpClient DefaultRequestHeaders");
                }
            }

            // Send request to get the payload
            var payloadResponse = await SendJsonRpcRequest(getPayloadRequest);

            // Check for error in response
            if (payloadResponse.Error is not null)
            {
                // Check for unsupported method error
                if (payloadResponse.Error.Code == -32601 ||
                    (payloadResponse.Error.Message?.Contains("not supported") == true))
                {
                    throw new InvalidOperationException($"Method engine_getPayloadV4 is not supported: {payloadResponse.Error.Message}");
                }

                throw new InvalidOperationException($"Error getting payload: {payloadResponse.Error.Code} - {payloadResponse.Error.Message}");
            }

            if (payloadResponse.Result is JObject payload)
            {
                try
                {
                    // Create newPayload request from the payload
                    _logger.Info($"Creating newPayload request from payload with parentBeaconBlockRoot: {parentBeaconBlockRoot}");
                    var newPayloadRequest = CreateNewPayloadRequest(payload, parentBeaconBlockRoot);

                    // Copy auth headers to new request too
                    if (_httpClient.DefaultRequestHeaders.Contains("Authorization"))
                    {
                        var authHeaderValue = _httpClient.DefaultRequestHeaders.GetValues("Authorization").FirstOrDefault();
                        if (!string.IsNullOrEmpty(authHeaderValue))
                        {
                            newPayloadRequest.OriginalHeaders = new Dictionary<string, string>
                            {
                                { "Authorization", authHeaderValue }
                            };
                            _logger.Debug("Added Authorization header to newPayload request from HttpClient DefaultRequestHeaders");
                        }
                    }

                    // Send newPayload request
                    _logger.Debug($"Sending synthetic newPayload request: {newPayloadRequest}");
                    var newPayloadResponse = await SendJsonRpcRequest(newPayloadRequest);

                    // Check if newPayload resulted in an error
                    if (newPayloadResponse.Error is not null)
                    {
                        _logger.Warn($"Synthetic newPayload validation resulted in error: {newPayloadResponse.Error.Code} - {newPayloadResponse.Error.Message}");
                    }
                    else if (newPayloadResponse.Result is JObject resultObj && resultObj["status"]?.ToString() == "INVALID")
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
                    return JsonRpcResponse.CreateErrorResponse(payloadResponse.Id, -32603, $"Error in synthetic newPayload validation: {ex.Message}");
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
    /// <returns>A newPayload request</returns>
    private JsonRpcRequest CreateNewPayloadRequest(JObject payload, string? parentBeaconBlockRoot = null)
    {
        try
        {
            // Extract the executionPayload from the response
            var executionPayload = payload["executionPayload"] ?? payload;

            // Create an empty array for blobVersionedHashes (second parameter)
            var blobVersionedHashes = new JArray();

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
                        var hash = new Hash256(Bytes.FromHexString(blockHash));

                        // First try the fast-path TryGet method to avoid multiple lookups
                        if (_payloadTracker.TryGetParentBeaconBlockRoot(hash, out var trackedParentBeaconBlockRoot) &&
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
                _logger.Debug($"Using provided parentBeaconBlockRoot in engine_newPayloadV5: {parentBeaconBlockRoot}");
            }

            // Create the parameter array for the newPayload request
            var parameters = new JArray
            {
                executionPayload,          // First parameter: executionPayload
                blobVersionedHashes,       // Second parameter: blobVersionedHashes
                parentBeaconBlockRoot,     // Third parameter: parentBeaconBlockRoot
                new JArray()               // Fourth parameter: execution_payload_preparation_info
            };

            _logger.Debug($"Created engine_newPayloadV4 request with parameters structure: executionPayload, blobVersionedHashes, parentBeaconBlockRoot: {parentBeaconBlockRoot}, empty preparation info");

            return new JsonRpcRequest(
                "engine_newPayloadV4",
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
            var requestJson = JsonConvert.SerializeObject(request);
            string targetHost = _httpClient.BaseAddress?.ToString() ?? "unknown";
            _logger.Debug($"Forwarding validation request to EL at: {targetHost}");
            _logger.Info($"PR -> EL|{request.Method}|V|{requestJson}");
            var httpContent = new StringContent(requestJson, Encoding.UTF8, "application/json");

            // Create a request message instead of using PostAsync
            var requestMessage = new HttpRequestMessage(HttpMethod.Post, "")
            {
                Content = httpContent
            };

            bool authHeaderAdded = false;

            // First, check if the request already has an Authorization header
            if (request.OriginalHeaders != null &&
                request.OriginalHeaders.TryGetValue("Authorization", out var requestAuthHeader) &&
                !string.IsNullOrEmpty(requestAuthHeader))
            {
                requestMessage.Headers.TryAddWithoutValidation("Authorization", requestAuthHeader);
                _logger.Debug($"Added Authorization header from request.OriginalHeaders: {requestAuthHeader.Substring(0, Math.Min(10, requestAuthHeader.Length))}...");
                authHeaderAdded = true;
            }

            // Next, try to get the Authorization header from the HttpClient's default headers
            if (!authHeaderAdded && _httpClient.DefaultRequestHeaders.Contains("Authorization"))
            {
                var httpClientAuthHeader = _httpClient.DefaultRequestHeaders.GetValues("Authorization").FirstOrDefault();
                if (!string.IsNullOrEmpty(httpClientAuthHeader))
                {
                    requestMessage.Headers.TryAddWithoutValidation("Authorization", httpClientAuthHeader);
                    _logger.Debug($"Added Authorization header from HttpClient.DefaultRequestHeaders: {httpClientAuthHeader.Substring(0, Math.Min(10, httpClientAuthHeader.Length))}...");
                    authHeaderAdded = true;
                }
            }

            // Then, copy any remaining original headers if present
            if (request.OriginalHeaders != null)
            {
                foreach (var header in request.OriginalHeaders)
                {
                    // Skip content-related headers and the Authorization header we already added
                    if (!header.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase))
                    {
                        requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                }
            }

            if (!authHeaderAdded)
            {
                _logger.Warn("No Authorization header found in either request.OriginalHeaders or HttpClient.DefaultRequestHeaders");
            }

            // Send request
            _logger.Debug($"Sending request with method: {request.Method}, HasAuth: {authHeaderAdded}, Target: {targetHost}");
            var response = await _httpClient.SendAsync(requestMessage);
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.Error($"HTTP request failed with status code: {response.StatusCode}, Content: {errorContent}");

                // Return an error response instead of throwing in tests
                return JsonRpcResponse.CreateErrorResponse(request.Id, -32603, $"Proxy error: HTTP error: {response.StatusCode}");
            }

            // Parse response
            var responseJson = await response.Content.ReadAsStringAsync();
            _logger.Debug($"Received response from EL at: {targetHost}");
            _logger.Info($"EL -> PR|{request.Method}|V|{responseJson}");

            try
            {
                var jsonRpcResponse = JsonConvert.DeserializeObject<JsonRpcResponse>(responseJson);
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
            return JsonRpcResponse.CreateErrorResponse(request.Id, -32603, $"Proxy error: Sending JSON-RPC request: {ex.Message}");
        }
    }

    /// <summary>
    /// Clones a JSON-RPC request
    /// </summary>
    /// <param name="request">The request to clone</param>
    /// <returns>A clone of the request</returns>
    private static JsonRpcRequest CloneRequest(JsonRpcRequest request)
    {
        return new JsonRpcRequest(
            request.Method,
            request.Params?.DeepClone() as JArray,
            request.Id)
        {
            OriginalHeaders = request.OriginalHeaders != null
                ? new Dictionary<string, string>(request.OriginalHeaders)
                : null
        };
    }

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
            var headBlock = _payloadTracker.GetHeadBlock(payloadId);
            if (headBlock is null)
            {
                _logger.Error($"No head block found for payloadId {payloadId}");
                return false;
            }

            // Get the payload using the existing method
            var payloadResponse = await GetAndProcessPayload(payloadId, headBlock);

            // Extract the block hash from the response
            if (payloadResponse.Result is JObject payload)
            {
                var executionPayload = payload["executionPayload"]?.ToObject<JObject>();
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
