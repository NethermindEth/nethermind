//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Synchronization.BeamSync;
using Newtonsoft.Json;
using JsonSerializer = Newtonsoft.Json.JsonSerializer;

namespace Nethermind.JsonRpc
{
    [Todo(Improve.Refactor, "Use JsonConverters and JSON serialization everywhere")]
    public class JsonRpcService : IJsonRpcService
    {
        public const string JsonRpcVersion = "2.0";

        private readonly ILogger _logger;
        private readonly IRpcModuleProvider _rpcModuleProvider;
        private readonly JsonSerializer _serializer;

        public JsonRpcService(IRpcModuleProvider rpcModuleProvider, ILogManager logManager)
        {
            _logger = logManager.GetClassLogger();
            _rpcModuleProvider = rpcModuleProvider;
            _serializer = new JsonSerializer();

            List<JsonConverter> converterList = new List<JsonConverter>();
            foreach (JsonConverter converter in rpcModuleProvider.Converters)
            {
                if (_logger.IsDebug) _logger.Debug($"Registering {converter.GetType().Name} inside {nameof(JsonRpcService)}");
                _serializer.Converters.Add(converter);
                converterList.Add(converter);
            }

            foreach (JsonConverter converter in EthereumJsonSerializer.CommonConverters)
            {
                if (_logger.IsDebug) _logger.Debug($"Registering {converter.GetType().Name} (default)");
                _serializer.Converters.Add(converter);
                converterList.Add(converter);
            }

            BlockParameterConverter blockParameterConverter = new BlockParameterConverter();
            _serializer.Converters.Add(blockParameterConverter);
            converterList.Add(blockParameterConverter);

            Converters = converterList.ToArray();
        }

        public async Task<JsonRpcResponse> SendRequestAsync(JsonRpcRequest rpcRequest)
        {
            try
            {
                (int? errorCode, string errorMessage) = Validate(rpcRequest);
                if (errorCode.HasValue)
                {
                    return GetErrorResponse(rpcRequest.Method, errorCode.Value, errorMessage, null, rpcRequest.Id);
                }

                try
                {
                    return await ExecuteRequestAsync(rpcRequest);
                }
                catch (TargetInvocationException ex)
                {
                    if (_logger.IsError) _logger.Error($"Error during method execution, request: {rpcRequest}", ex.InnerException);
                    return GetErrorResponse(rpcRequest.Method, ErrorCodes.InternalError, "Internal error", ex.InnerException?.ToString(), rpcRequest.Id);
                }
                catch (Exception ex)
                {
                    if (_logger.IsError) _logger.Error($"Error during method execution, request: {rpcRequest}", ex);
                    return GetErrorResponse(rpcRequest.Method, ErrorCodes.InternalError, "Internal error", ex.ToString(), rpcRequest.Id);
                }
            }
            catch (Exception ex)
            {
                if (_logger.IsError) _logger.Error($"Error during validation, request: {rpcRequest}", ex);
                return GetErrorResponse(null, ErrorCodes.ParseError, "Parse error", null, null);
            }
        }

        private async Task<JsonRpcResponse> ExecuteRequestAsync(JsonRpcRequest rpcRequest)
        {
            string methodName = rpcRequest.Method.Trim().ToLower();

            (MethodInfo MethodInfo, bool ReadOnly) result = _rpcModuleProvider.Resolve(methodName);
            if (result.MethodInfo != null)
            {
                return await ExecuteAsync(rpcRequest, methodName, result);
            }

            return GetErrorResponse(methodName, ErrorCodes.MethodNotFound, "Method not found", $"{rpcRequest.Method}", rpcRequest.Id);
        }

        private async Task<JsonRpcResponse> ExecuteAsync(JsonRpcRequest request, string methodName, (MethodInfo Info, bool ReadOnly) method)
        {
            var expectedParameters = method.Info.GetParameters();
            var providedParameters = request.Params;
            if (_logger.IsInfo) _logger.Info($"Executing JSON RPC call {methodName}{(providedParameters == null ? string.Empty : $" with params {string.Join(',', providedParameters)}")}");

            int missingParamsCount = expectedParameters.Length - (providedParameters?.Length ?? 0) + providedParameters?.Count(string.IsNullOrWhiteSpace) ?? 0;

            if (missingParamsCount != 0)
            {
                bool incorrectParametersCount = missingParamsCount != 0;
                if (missingParamsCount > 0)
                {
                    incorrectParametersCount = false;
                    for (int i = 0; i < missingParamsCount; i++)
                    {
                        if (!expectedParameters[expectedParameters.Length - missingParamsCount + i].IsOptional)
                        {
                            incorrectParametersCount = true;
                            break;
                        }
                    }
                }

                if (incorrectParametersCount)
                {
                    return GetErrorResponse(methodName, ErrorCodes.InvalidParams, "Invalid params", $"Incorrect parameters count, expected: {expectedParameters.Length}, actual: {expectedParameters.Length - missingParamsCount}", request.Id);
                }
            }

            //prepare parameters
            object[] parameters = null;
            if (expectedParameters.Length > 0)
            {
                parameters = DeserializeParameters(expectedParameters, providedParameters, missingParamsCount);
                if (parameters == null)
                {
                    if (_logger.IsWarn) _logger.Warn($"Incorrect JSON RPC parameters when calling {methodName}: {string.Join(", ", providedParameters ?? new string[0])}");
                    return GetErrorResponse(methodName, ErrorCodes.InvalidParams, "Invalid params", null, request.Id);
                }
            }

            //execute method
            IResultWrapper resultWrapper = null;
            IModule module = _rpcModuleProvider.Rent(methodName, method.ReadOnly);
            try
            {
                BeamSyncContext.LastFetchUtc.Value = DateTime.UtcNow;
                BeamSyncContext.Description.Value = $"[JSON RPC {methodName}]";

                object invocationResult = method.Info.Invoke(module, parameters);
                switch (invocationResult)
                {
                    case IResultWrapper wrapper:
                        resultWrapper = wrapper;
                        break;
                    case Task task:
                        await task;
                        resultWrapper = task.GetType().GetProperty("Result")?.GetValue(task) as IResultWrapper;
                        break;
                }
            }
            catch (TargetParameterCountException e)
            {
                return GetErrorResponse(methodName, ErrorCodes.InvalidParams, e.Message, e.Data, request.Id);
            }
            catch (TargetInvocationException invocationException) when (invocationException.InnerException is BeamSyncException beamSyncException)
            {
                return GetErrorResponse(methodName, ErrorCodes.ResourceUnavailable, beamSyncException.Message, invocationException.Data, request.Id);
            }
            finally
            {
                _rpcModuleProvider.Return(methodName, module);
            }

            if (resultWrapper is null)
            {
                string errorMessage = $"Method {methodName} execution result does not implement IResultWrapper";
                if (_logger.IsError) _logger.Error(errorMessage);
                return GetErrorResponse(methodName, ErrorCodes.InternalError, errorMessage, null, request.Id);
            }

            Result result = resultWrapper.GetResult();
            if (result == null)
            {
                if (_logger.IsError) _logger.Error($"Error during method: {methodName} execution: no result");
                return GetErrorResponse(methodName, resultWrapper.GetErrorCode(), "Internal error", resultWrapper.GetData(), request.Id);
            }

            if (result.ResultType == ResultType.Failure)
            {
                return GetErrorResponse(methodName, resultWrapper.GetErrorCode(), resultWrapper.GetResult().Error, resultWrapper.GetData(), request.Id);
            }

            return GetSuccessResponse(resultWrapper.GetData(), request.Id);
        }

        private object[] DeserializeParameters(ParameterInfo[] expectedParameters, string[] providedParameters, int missingParamsCount)
        {
            try
            {
                var executionParameters = new List<object>();
                for (int i = 0; i < providedParameters.Length; i++)
                {
                    string providedParameter = providedParameters[i];
                    ParameterInfo expectedParameter = expectedParameters[i];
                    Type paramType = expectedParameter.ParameterType;
                    if (paramType.IsByRef)
                    {
                        paramType = paramType.GetElementType();
                    }

                    if (string.IsNullOrWhiteSpace(providedParameter))
                    {
                        executionParameters.Add(Type.Missing);
                        continue;
                    }

                    object executionParam;
                    if (typeof(IJsonRpcRequest).IsAssignableFrom(paramType))
                    {
                        executionParam = Activator.CreateInstance(paramType);
                        ((IJsonRpcRequest) executionParam).FromJson(providedParameter);
                    }
                    else if (paramType == typeof(string))
                    {
                        executionParam = providedParameter;
                    }
                    else if (paramType == typeof(string[]))
                    {
                        executionParam = _serializer.Deserialize<string[]>(new JsonTextReader(new StringReader(providedParameter)));
                    }
                    else
                    {
                        if (providedParameter.StartsWith('[') || providedParameter.StartsWith('{'))
                        {
                            executionParam = _serializer.Deserialize(new JsonTextReader(new StringReader(providedParameter)), paramType);
                        }
                        else
                        {
                            executionParam = _serializer.Deserialize(new JsonTextReader(new StringReader($"\"{providedParameter}\"")), paramType);
                        }
                    }

                    executionParameters.Add(executionParam);
                }

                for (int i = 0; i < missingParamsCount; i++)
                {
                    executionParameters.Add(Type.Missing);
                }

                return executionParameters.ToArray();
            }
            catch (Exception e)
            {
                if (_logger.IsWarn) _logger.Warn("Error while parsing JSON RPC request parameters " + e);
                return null;
            }
        }

        private JsonRpcResponse GetSuccessResponse(object result, object id)
        {
            JsonRpcResponse response = new JsonRpcSuccessResponse
            {
                Result = result,
                Id = id
            };

            return response;
        }

        public JsonRpcErrorResponse GetErrorResponse(int errorCode, string errorMessage)
        {
            return GetErrorResponse(null, errorCode, errorMessage, null, null);
        }

        public JsonConverter[] Converters { get; }

        private JsonRpcErrorResponse GetErrorResponse(string methodName, int errorCode, string errorMessage, object errorData, object id)
        {
            if (_logger.IsDebug) _logger.Debug($"Sending error response, method: {methodName ?? "none"}, id: {id}, errorType: {errorCode}, message: {errorMessage}");
            JsonRpcErrorResponse response = new JsonRpcErrorResponse
            {
                Error = new Error
                {
                    Code = errorCode,
                    Message = errorMessage,
                    Data = errorData,
                },
                Id = id
            };

            return response;
        }

        private (int? ErrorType, string ErrorMessage) Validate(JsonRpcRequest rpcRequest)
        {
            if (rpcRequest == null)
            {
                return (ErrorCodes.InvalidRequest, "Invalid request");
            }

            string methodName = rpcRequest.Method;
            if (string.IsNullOrWhiteSpace(methodName))
            {
                return (ErrorCodes.InvalidRequest, "Method is required");
            }

            methodName = methodName.Trim().ToLower();

            ModuleResolution result = _rpcModuleProvider.Check(methodName);
            return result switch
            {
                ModuleResolution.Unknown => ((int?) ErrorCodes.MethodNotFound, $"Method {methodName} is not supported"),
                ModuleResolution.Disabled => (ErrorCodes.InvalidRequest, $"{methodName} found but the containing module is disabled"),
                _ => (null, null)
            };
        }
    }
}