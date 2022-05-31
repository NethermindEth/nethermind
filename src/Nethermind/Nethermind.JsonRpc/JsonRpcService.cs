//  Copyright (c) 2021 Demerzel Solutions Limited
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
using Nethermind.State;
using Newtonsoft.Json;
using JsonSerializer = Newtonsoft.Json.JsonSerializer;

namespace Nethermind.JsonRpc
{
    [Todo(Improve.Refactor, "Use JsonConverters and JSON serialization everywhere")]
    public class JsonRpcService : IJsonRpcService
    {
        private readonly ILogger _logger;
        private readonly IRpcModuleProvider _rpcModuleProvider;
        private readonly JsonSerializer _serializer;
        private readonly IJsonRpcConfig _jsonRpcConfig;

        public JsonRpcService(IRpcModuleProvider rpcModuleProvider, ILogManager logManager, IJsonRpcConfig jsonRpcConfig)
        {
            _logger = logManager.GetClassLogger();
            _rpcModuleProvider = rpcModuleProvider;
            _serializer = rpcModuleProvider.Serializer;
            _jsonRpcConfig = jsonRpcConfig ?? throw new ArgumentNullException(nameof(jsonRpcConfig));

            List<JsonConverter> converterList = new();
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

            BlockParameterConverter blockParameterConverter = new();
            _serializer.Converters.Add(blockParameterConverter);
            converterList.Add(blockParameterConverter);

            Converters = converterList.ToArray();
        }

        public async Task<JsonRpcResponse> SendRequestAsync(JsonRpcRequest rpcRequest, JsonRpcContext context)
        {
            try
            {
                (int? errorCode, string errorMessage) = Validate(rpcRequest, context);
                if (errorCode.HasValue)
                {
                    return GetErrorResponse(rpcRequest.Method, errorCode.Value, errorMessage, null, rpcRequest.Id);
                }

                try
                {
                    return await ExecuteRequestAsync(rpcRequest, context);
                }
                catch (TargetInvocationException ex)
                {
                    if (_logger.IsError) _logger.Error($"Error during method execution, request: {rpcRequest}", ex.InnerException);
                    return GetErrorResponse(rpcRequest.Method, ErrorCodes.InternalError, "Internal error", ex.InnerException?.ToString(), rpcRequest.Id);
                }
                catch (ModuleRentalTimeoutException ex)
                {
                    if (_logger.IsError) _logger.Error($"Error during method execution, request: {rpcRequest}", ex);
                    return GetErrorResponse(rpcRequest.Method, ErrorCodes.ModuleTimeout, "Timeout", ex.ToString(), rpcRequest.Id);
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
                return GetErrorResponse(ErrorCodes.ParseError, "Parse error");
            }
        }

        private async Task<JsonRpcResponse> ExecuteRequestAsync(JsonRpcRequest rpcRequest, JsonRpcContext context)
        {
            string methodName = rpcRequest.Method.Trim();

            (MethodInfo MethodInfo, bool ReadOnly) result = _rpcModuleProvider.Resolve(methodName);
            return result.MethodInfo != null 
                ? await ExecuteAsync(rpcRequest, methodName, result, context) 
                : GetErrorResponse(methodName, ErrorCodes.MethodNotFound, "Method not found", $"{rpcRequest.Method}", rpcRequest.Id);
        }

        private async Task<JsonRpcResponse> ExecuteAsync(JsonRpcRequest request, string methodName,
            (MethodInfo Info, bool ReadOnly) method, JsonRpcContext context)
        {
            ParameterInfo[] expectedParameters = method.Info.GetParameters();
            string?[] providedParameters = request.Params ?? Array.Empty<string>();
            
            LogRequest(methodName, providedParameters);

            int missingParamsCount = expectedParameters.Length - providedParameters.Length + (providedParameters.Count(string.IsNullOrWhiteSpace));
            int explicitNullableParamsCount = 0;

            if (missingParamsCount != 0)
            {
                bool hasIncorrectParameters = true;
                if (missingParamsCount > 0)
                {
                    hasIncorrectParameters = false;
                    for (int i = 0; i < missingParamsCount; i++)
                    {
                        int parameterIndex = expectedParameters.Length - missingParamsCount + i;
                        bool nullable =
                            IsNullableParameter(expectedParameters[parameterIndex]);
                        
                        // if the null is the default parameter it could be passed in an explicit way as "" or null
                        // or we can treat null as a missing parameter. Two tests for this cases:
                        // Eth_call_is_working_with_implicit_null_as_the_last_argument and Eth_call_is_working_with_explicit_null_as_the_last_argument
                        bool isExplicit = providedParameters.Length >= parameterIndex + 1;
                        if (nullable && isExplicit)
                        {
                            explicitNullableParamsCount += 1;
                        }
                        if (!expectedParameters[expectedParameters.Length - missingParamsCount + i].IsOptional && !nullable)
                        {
                            hasIncorrectParameters = true;
                            break;
                        }
                    }
                }

                if (hasIncorrectParameters)
                {
                    return GetErrorResponse(methodName, ErrorCodes.InvalidParams, "Invalid params", $"Incorrect parameters count, expected: {expectedParameters.Length}, actual: {expectedParameters.Length - missingParamsCount}", request.Id);
                }
            }

            missingParamsCount -= explicitNullableParamsCount;

            //prepare parameters
            object[]? parameters = null;
            if (expectedParameters.Length > 0)
            {
                parameters = DeserializeParameters(expectedParameters, providedParameters, missingParamsCount);
                if (parameters == null)
                {
                    if (_logger.IsWarn) _logger.Warn($"Incorrect JSON RPC parameters when calling {methodName} with params [{string.Join(", ", providedParameters)}]");
                    return GetErrorResponse(methodName, ErrorCodes.InvalidParams, "Invalid params", null, request.Id);
                }
            }

            //execute method
            IResultWrapper resultWrapper = null;
            IRpcModule rpcModule = await _rpcModuleProvider.Rent(methodName, method.ReadOnly);
            if (rpcModule is IContextAwareRpcModule contextAwareModule)
            {
                contextAwareModule.Context = context;
            }
            bool returnImmediately = methodName != "eth_getLogs";
            Action? returnAction = returnImmediately ? (Action) null : () => _rpcModuleProvider.Return(methodName, rpcModule);
            try
            {
                object invocationResult = method.Info.Invoke(rpcModule, parameters);
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
                return GetErrorResponse(methodName, ErrorCodes.InvalidParams, e.Message, e.Data, request.Id, returnAction);
            }
            catch (TargetInvocationException e) when (e.InnerException is JsonException)
            {
                return GetErrorResponse(methodName, ErrorCodes.InvalidParams, "Invalid params", null, request.Id, returnAction);
            }            
            catch (Exception e) when (e.InnerException is OperationCanceledException)
            {
                string errorMessage = $"{methodName} request was canceled due to enabled timeout.";
                return GetErrorResponse(methodName, ErrorCodes.Timeout, errorMessage, null, request.Id, returnAction);
            }
            catch (Exception e) when (e.InnerException is InsufficientBalanceException)
            {
                return GetErrorResponse(methodName, ErrorCodes.InvalidInput, e.InnerException.Message, null, request.Id, returnAction);
            }
            finally
            {
                if (returnImmediately)
                {
                    _rpcModuleProvider.Return(methodName, rpcModule);
                }
            }

            if (resultWrapper is null)
            {
                string errorMessage = $"Method {methodName} execution result does not implement IResultWrapper";
                if (_logger.IsError) _logger.Error(errorMessage);
                return GetErrorResponse(methodName, ErrorCodes.InternalError, errorMessage, null, request.Id, returnAction);
            }

            Result? result = resultWrapper.GetResult();
            if (result == null)
            {
                if (_logger.IsError) _logger.Error($"Error during method: {methodName} execution: no result");
                return GetErrorResponse(methodName, resultWrapper.GetErrorCode(), "Internal error", resultWrapper.GetData(), request.Id, returnAction);
            }

            if (result.ResultType == ResultType.Failure)
            {
                return GetErrorResponse(methodName, resultWrapper.GetErrorCode(), result.Error, resultWrapper.GetData(), request.Id, returnAction);
            }

            return GetSuccessResponse(methodName, resultWrapper.GetData(), request.Id, returnAction);
        }

        private void LogRequest(string methodName, string?[] providedParameters)
        {
            //TODO: Move to a dictionary.
            if (_logger.IsInfo && (_jsonRpcConfig.MethodsLoggingFiltering == null || !_jsonRpcConfig.MethodsLoggingFiltering.Contains(methodName)))
            {
                string paramStr = string.Join(',', providedParameters);
                string paramStrAdjusted = paramStr[..Math.Min(paramStr.Length, _jsonRpcConfig.MaxLoggedRequestParametersCharacters ?? paramStr.Length)];
                if (paramStrAdjusted.Length < paramStr.Length) paramStrAdjusted += "...";
                _logger.Info($"Executing JSON RPC call {methodName} with params [{paramStrAdjusted}]");
            }
        }

        private object[]? DeserializeParameters(ParameterInfo[] expectedParameters, string?[] providedParameters, int missingParamsCount)
        {
            try
            {
                List<object> executionParameters = new List<object>();
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
                        if (providedParameter == null && IsNullableParameter(expectedParameter))
                        {
                            executionParameters.Add(null);
                        }
                        else
                        {
                            executionParameters.Add(Type.Missing);
                        }
                        continue;
                    }

                    object? executionParam;
                    if (typeof(IJsonRpcParam).IsAssignableFrom(paramType))
                    {
                        IJsonRpcParam jsonRpcParam = (IJsonRpcParam)Activator.CreateInstance(paramType);
                        jsonRpcParam!.FromJson(_serializer, providedParameter);
                        executionParam = jsonRpcParam;
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

        private bool IsNullableParameter(ParameterInfo parameterInfo)
        {
            Type parameterType = parameterInfo.ParameterType;
            if (parameterType.IsValueType)
            {
                return Nullable.GetUnderlyingType(parameterType) != null;
            }

            CustomAttributeData nullableAttribute = parameterInfo.CustomAttributes
                .FirstOrDefault(x => x.AttributeType.FullName == "System.Runtime.CompilerServices.NullableAttribute");
            if (nullableAttribute != null)
            {
                CustomAttributeTypedArgument attributeArgument = nullableAttribute.ConstructorArguments.FirstOrDefault();
                if (attributeArgument.ArgumentType == typeof(byte))
                {
                    return (byte)attributeArgument.Value! == 2;
                }
            }
            return false;
        }

        private JsonRpcResponse GetSuccessResponse(string methodName, object result, object id, Action? disposableAction)
        {
            JsonRpcResponse response = new JsonRpcSuccessResponse(disposableAction)
            {
                Result = result,
                Id = id,
                MethodName = methodName
            };

            return response;
        }

        public JsonRpcErrorResponse GetErrorResponse(int errorCode, string errorMessage) => 
            GetErrorResponse(null, errorCode, errorMessage, null, null);

        public JsonConverter[] Converters { get; }

        private JsonRpcErrorResponse GetErrorResponse(string? methodName, int errorCode, string? errorMessage, object? errorData, object? id, Action? disposableAction = null)
        {
            if (_logger.IsDebug) _logger.Debug($"Sending error response, method: {methodName ?? "none"}, id: {id}, errorType: {errorCode}, message: {errorMessage}");
            JsonRpcErrorResponse response = new(disposableAction)
            {
                Error = new Error
                {
                    Code = errorCode,
                    Message = errorMessage,
                    Data = errorData,
                },
                Id = id,
                MethodName = methodName
            };

            return response;
        }

        private (int? ErrorType, string ErrorMessage) Validate(JsonRpcRequest? rpcRequest, JsonRpcContext context)
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

            methodName = methodName.Trim();

            ModuleResolution result = _rpcModuleProvider.Check(methodName, context);
            return result switch
            {
                ModuleResolution.Unknown => ((int?) ErrorCodes.MethodNotFound, $"Method {methodName} is not supported"),
                ModuleResolution.Disabled => (ErrorCodes.InvalidRequest, $"{methodName} found but the containing module is disabled for the url '{context.Url?.ToString() ?? string.Empty}', consider adding module in JsonRpcConfig.AdditionalRpcUrls for additional url, or to JsonRpcConfig.EnabledModules for default url"),
                ModuleResolution.EndpointDisabled => (ErrorCodes.InvalidRequest, $"{methodName} found for the url '{context.Url?.ToString() ?? string.Empty}' but is disabled for {context.RpcEndpoint}"),
                ModuleResolution.NotAuthenticated => (ErrorCodes.InvalidRequest, $"Method {methodName} should be authenticated"),
                _ => (null, null)
            };
        }
    }
}
