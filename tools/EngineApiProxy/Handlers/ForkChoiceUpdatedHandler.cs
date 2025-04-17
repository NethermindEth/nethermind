using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.EngineApiProxy.Config;
using Nethermind.EngineApiProxy.Models;
using Nethermind.EngineApiProxy.Services;
using Nethermind.EngineApiProxy.Utilities;
using Nethermind.Logging;
using Newtonsoft.Json.Linq;

namespace Nethermind.EngineApiProxy.Handlers
{
    public class ForkChoiceUpdatedHandler(
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
            if (_config.ValidationMode == ValidationMode.NewPayload)
            {
                _logger.Debug($"Processing engine_forkchoiceUpdated (NewPayload flow): {request}");
                return await _requestForwarder.ForwardRequestToExecutionClient(request);
            }
            else if (_config.ValidationMode == ValidationMode.Merged)
            {
                // Log the request
                _logger.Info("---------------(Merged flow)-----------------");
                _logger.Debug($"Processing engine_forkchoiceUpdated (Merged flow): {request}");
                
                try
                {
                    // Check if we should validate this block
                    bool shouldValidate = ShouldValidateBlock(request);
                    _logger.Info($"ShouldValidateBlock for FCU: {shouldValidate} for Merged mode");
                    
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

            // Log the forkChoiceUpdated request
            _logger.Info("---------------(FCU flow)-----------------");
            _logger.Debug($"Processing engine_forkchoiceUpdated (FCU flow): {request}");
            
            try
            {
                // Check if we should validate this block
                bool shouldValidate = ShouldValidateBlock(request);
                _logger.Info($"ShouldValidateBlock for FCU: {shouldValidate}");
                
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
                                  request.Params != null && 
                                  request.Params.Count > 0 &&
                                  (request.Params.Count == 1 || 
                                   request.Params[1] == null || 
                                   (request.Params[1] is JValue jv && jv.Type == JTokenType.Null) ||
                                   (request.Params[1]?.Type == JTokenType.Null)) &&
                                  !(request.Params.Count > 1 && request.Params[1] is JObject);
            
            // Add detailed logging to show validation decision
            if (_config.ValidateAllBlocks) 
            {
                _logger.Debug($"ValidateAllBlocks is enabled, params count: {request.Params?.Count}, second param type: {(request.Params?.Count > 1 ? request.Params[1]?.GetType().Name : "none")}");
                
                if (request.Params?.Count > 1 && request.Params[1] is JObject)
                {
                    _logger.Info("Skipping validation because request already contains payload attributes");
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
                    if (ex.ToString().Contains("engine_getPayloadV4 is not supported") ||
                        ex.ToString().Contains("The method 'engine_getPayloadV4' is not supported") ||
                        ex.ToString().Contains("engine_getPayloadV3 is not supported") ||
                        ex.ToString().Contains("The method 'engine_getPayloadV3' is not supported"))
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
                resultObj["payloadId"] != null && 
                request.Params != null && 
                request.Params.Count > 0 && 
                request.Params[0] is JObject fcState &&
                fcState["headBlockHash"] != null)
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
                payloadId = await _requestOrchestrator.GetPayloadID(request, headBlockHashStr);
                
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
                
                // // Store info for validation that will happen after response
                // string storedPayloadId = payloadId;
                
                // // 3. Trigger validation AFTER returning response to CL
                // _ = Task.Run(async () => 
                // {
                //     try 
                //     {
                //         if (!string.IsNullOrEmpty(storedPayloadId))
                //         {
                //             // Wait a moment to ensure response is sent
                //             await Task.Delay(200);
                            
                //             _logger.Info($"Starting post-response validation for payloadId {storedPayloadId}");
                //             bool success = await _requestOrchestrator.DoValidationForFCU(storedPayloadId);
                //             _logger.Info($"Post-response validation for payloadId {storedPayloadId} completed: {success}");
                //         }
                //     }
                //     catch (Exception ex) 
                //     {
                //         _logger.Error($"Error in post-response validation: {ex.Message}", ex);
                //     }
                // });
                
                return response;
            }
            catch (Exception ex)
            {
                _logger.Error($"Error handling merged FCU: {ex.Message}", ex);
                return JsonRpcResponse.CreateErrorResponse(request.Id, -32603, $"Proxy error handling merged FCU: {ex.Message}");
            }
        }

        private static string ExtractHeadBlockHash(JsonRpcRequest request)
        {
            if (request.Params?[0] is JObject forkChoiceState && 
                forkChoiceState["headBlockHash"] != null)
            {
                return forkChoiceState["headBlockHash"]?.ToString() ?? string.Empty;
            }
            
            return string.Empty;
        }
    }
} 