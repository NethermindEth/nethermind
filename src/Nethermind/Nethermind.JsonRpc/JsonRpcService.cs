/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Json;
using Nethermind.Core.Model;
using Nethermind.Dirichlet.Numerics;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Newtonsoft.Json;
using JsonSerializer = Newtonsoft.Json.JsonSerializer;

namespace Nethermind.JsonRpc
{
    [Todo(Improve.Refactor, "Use JsonConverters and JSON serialization everywhere")]
    public class JsonRpcService : IJsonRpcService
    {
        public static IDictionary<ErrorType, int> ErrorCodes => new Dictionary<ErrorType, int>
        {
            {ErrorType.ParseError, -32700},
            {ErrorType.InvalidRequest, -32600},
            {ErrorType.MethodNotFound, -32601},
            {ErrorType.InvalidParams, -32602},
            {ErrorType.InternalError, -32603},
            {ErrorType.ExecutionError, -32015},
            {ErrorType.NotFound, -32601}, // ??
        };

        public const string JsonRpcVersion = "2.0";

        private readonly ILogger _logger;
        private readonly IRpcModuleProvider _rpcModuleProvider;
        private readonly JsonSerializer _serializer;

        private Dictionary<Type, JsonConverter> _converterLookup = new Dictionary<Type, JsonConverter>();

        public JsonRpcService(IRpcModuleProvider rpcModuleProvider, ILogManager logManager)
        {
            _logger = logManager.GetClassLogger();
            _rpcModuleProvider = rpcModuleProvider;
            _serializer = new JsonSerializer();

            foreach (JsonConverter converter in rpcModuleProvider.Converters)
            {
                if (_logger.IsDebug) _logger.Debug($"Registering {converter.GetType().Name} inside {nameof(JsonRpcService)}");
                _serializer.Converters.Add(converter);
                _converterLookup.Add(converter.GetType().BaseType.GenericTypeArguments[0], converter);
                Converters.Add(converter);
            }

            foreach (JsonConverter converter in EthereumJsonSerializer.BasicConverters)
            {
                if (_logger.IsDebug) _logger.Debug($"Registering {converter.GetType().Name} (default)");
                _serializer.Converters.Add(converter);
                _converterLookup.Add(converter.GetType().BaseType.GenericTypeArguments[0], converter);
                Converters.Add(converter);
            }
        }

        public async Task<JsonRpcResponse> SendRequestAsync(JsonRpcRequest rpcRequest)
        {
            try
            {
                (ErrorType? errorType, string errorMessage) = Validate(rpcRequest);
                if (errorType.HasValue)
                {
                    return GetErrorResponse(errorType.Value, errorMessage, rpcRequest.Id, rpcRequest.Method);
                }

                try
                {
                    return await ExecuteRequestAsync(rpcRequest);
                }
                catch (TargetInvocationException ex)
                {
                    if (_logger.IsError) _logger.Error($"Error during method execution, request: {rpcRequest}", ex.InnerException);
                    return GetErrorResponse(ErrorType.InternalError, ex.InnerException.ToString(), rpcRequest.Id, rpcRequest.Method);
                }
                catch (Exception ex)
                {
                    if (_logger.IsError) _logger.Error($"Error during method execution, request: {rpcRequest}", ex);
                    return GetErrorResponse(ErrorType.InternalError, ex.InnerException.ToString(), rpcRequest.Id, rpcRequest.Method);
                }
            }
            catch (Exception ex)
            {
                if (_logger.IsError) _logger.Error($"Error during validation, request: {rpcRequest}", ex);
                return GetErrorResponse(ErrorType.ParseError, "Incorrect message", 0, null);
            }
        }

        private async Task<JsonRpcResponse> ExecuteRequestAsync(JsonRpcRequest rpcRequest)
        {
            var methodName = rpcRequest.Method.Trim().ToLower();

            var result = _rpcModuleProvider.Resolve(methodName);
            if (result.MethodInfo != null)
            {
                return await ExecuteAsync(rpcRequest, methodName, result);
            }

            return GetErrorResponse(ErrorType.MethodNotFound, $"Method {rpcRequest.Method} is not supported", rpcRequest.Id, methodName);
        }

        private async Task<JsonRpcResponse> ExecuteAsync(JsonRpcRequest request, string methodName, (MethodInfo Info, bool ReadOnly) method)
        {
            var expectedParameters = method.Info.GetParameters();
            var providedParameters = request.Params;
            if(_logger.IsInfo) _logger.Info($"Executing JSON RPC call {methodName} with params {string.Join(',', providedParameters)}");
            
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
                    return GetErrorResponse(ErrorType.InvalidParams, $"Incorrect parameters count, expected: {expectedParameters.Length}, actual: {expectedParameters.Length - missingParamsCount}", request.Id, methodName);
                }
            }

            //prepare parameters
            object[] parameters = null;
            if (expectedParameters.Length > 0)
            {
                parameters = DeserializeParameters(expectedParameters, providedParameters, missingParamsCount);
                if (parameters == null)
                {
                    if (_logger.IsError) _logger.Error($"Incorrect JSON RPC parameters when calling {methodName}: {string.Join(", ", providedParameters)}");
                    return GetErrorResponse(ErrorType.InvalidParams, "Incorrect parameters", request.Id, methodName);
                }
            }

            //execute method
            IResultWrapper resultWrapper = null;
            IModule module = _rpcModuleProvider.Rent(methodName, method.ReadOnly);
            try
            {
                var invocationResult = method.Info.Invoke(module, parameters);
                if (invocationResult is IResultWrapper wrapper)
                {
                    resultWrapper = wrapper;
                }
                else if (invocationResult is Task task)
                {
                    await task;
                    resultWrapper = task.GetType().GetProperty("Result").GetValue(task) as IResultWrapper;
                }
            }
            finally
            {
                _rpcModuleProvider.Return(methodName, module);
            }

            if (resultWrapper is null)
            {
                string errorMessage = $"Method {methodName} execution result does not implement IResultWrapper";
                if (_logger.IsError) _logger.Error(errorMessage);
                return GetErrorResponse(ErrorType.InternalError, errorMessage, request.Id, methodName);
            }

            Result result = resultWrapper.GetResult();
            if (result == null || result.ResultType == ResultType.Failure)
            {
                if (_logger.IsError) _logger.Error($"Error during method: {methodName} execution: {result?.Error ?? "no result"}");
                return GetErrorResponse(resultWrapper.GetErrorType(), resultWrapper.GetResult().Error, request.Id, methodName, resultWrapper.GetData());
            }

            return GetSuccessResponse(resultWrapper.GetData(), request.Id);
        }

        private object[] DeserializeParameters(ParameterInfo[] expectedParameters, string[] providedParameters, int missingParamsCount)
        {
            try
            {
                var executionParameters = new List<object>();
                for (var i = 0; i < providedParameters.Length; i++)
                {
                    var providedParameter = providedParameters[i];
                    var expectedParameter = expectedParameters[i];
                    var paramType = expectedParameter.ParameterType;
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
                            executionParam = JsonConvert.DeserializeObject(providedParameter, paramType, Converters.ToArray());
                        }
                        else
                        {
                            executionParam = JsonConvert.DeserializeObject($"\"{providedParameter}\"", paramType, Converters.ToArray());
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
                if (_logger.IsError) _logger.Error("Error while parsing parameters", e);
                return null;
            }
        }

        private JsonRpcResponse GetSuccessResponse(object result, UInt256 id)
        {
            var response = new JsonRpcResponse
            {
                Id = id,
                JsonRpc = JsonRpcVersion,
                Result = result,
            };

            return response;
        }

        public JsonRpcErrorResponse GetErrorResponse(ErrorType errorType, string message)
        {
            return GetErrorResponse(errorType, message, 0, null);
        }

        public IList<JsonConverter> Converters { get; } = new List<JsonConverter>();

        private JsonRpcErrorResponse GetErrorResponse(ErrorType errorType, string message, UInt256 id, string methodName, object result = null)
        {
            if (_logger.IsDebug) _logger.Debug($"Sending error response, method: {methodName ?? "none"}, id: {id}, errorType: {errorType}, message: {message}");
            var response = new JsonRpcErrorResponse
            {
                JsonRpc = JsonRpcVersion,
                Id = id,
                Error = new Error
                {
                    Code = ErrorCodes[errorType],
                    Message = message
                },
                Result = result
            };

            return response;
        }

        private (ErrorType? ErrorType, string ErrorMessage) Validate(JsonRpcRequest rpcRequest)
        {
            if (rpcRequest == null)
            {
                return (ErrorType.InvalidRequest, "Invalid request");
            }

            var methodName = rpcRequest.Method;
            if (string.IsNullOrWhiteSpace(methodName))
            {
                return (ErrorType.InvalidRequest, "Method is required");
            }

            methodName = methodName.Trim().ToLower();

            var result = _rpcModuleProvider.Check(methodName);
            if (result == ModuleResolution.Unknown)
            {
                return (ErrorType.MethodNotFound, $"Method {methodName} is not supported");
            }

            if (result == ModuleResolution.Disabled)
            {
                return (ErrorType.InvalidRequest, $"{methodName} found but the containing module is disabled");
            }

            return (null, null);
        }
    }
}