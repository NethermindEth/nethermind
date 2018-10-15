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
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices.WindowsRuntime;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Logging;
using Nethermind.Core.Model;
using Nethermind.JsonRpc.Config;
using Nethermind.JsonRpc.DataModel;
using Nethermind.JsonRpc.Module;

namespace Nethermind.JsonRpc
{
    public class JsonRpcService : IJsonRpcService
    {
        private readonly ILogger _logger;
        private readonly IJsonRpcConfig _jsonRpcConfig;
        private readonly IModuleProvider _moduleProvider;

        public JsonRpcService(IModuleProvider moduleProvider, IConfigProvider configurationProvider, ILogManager logManager)
        {
            _logger = logManager.GetClassLogger();
            _jsonRpcConfig = configurationProvider.GetConfig<IJsonRpcConfig>();
            _moduleProvider = moduleProvider;
        }

        public JsonRpcResponse SendRequest(JsonRpcRequest rpcRequest)
        {
            try
            {
                (ErrorType? errorType, string errorMessage) = Validate(rpcRequest);
                if (errorType.HasValue)
                {
                    return GetErrorResponse(errorType.Value, errorMessage, rpcRequest.Id, rpcRequest?.Method);
                }
                try
                {
                    return ExecuteRequest(rpcRequest);
                }
                catch (TargetInvocationException ex)
                {
                    _logger.Error($"Error during method execution, request: {rpcRequest}", ex.InnerException);
                    return GetErrorResponse(ErrorType.InternalError, "Internal error", rpcRequest.Id, rpcRequest?.Method);
                }
                catch (Exception ex)
                {
                    _logger.Error($"Error during method execution, request: {rpcRequest}", ex);
                    return GetErrorResponse(ErrorType.InternalError, "Internal error", rpcRequest.Id, rpcRequest?.Method);
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Error during validation, request: {rpcRequest}", ex);
                return GetErrorResponse(ErrorType.ParseError, "Incorrect message", 0, null);
            }         
        }
        private JsonRpcResponse ExecuteRequest(JsonRpcRequest rpcRequest)
        {
            var methodName = rpcRequest.Method.Trim().ToLower();
            
            var module = _moduleProvider.GetEnabledModules().FirstOrDefault(x => x.MethodDictionary.ContainsKey(methodName));
            if (module != null)
            {
                return Execute(rpcRequest, methodName, module.MethodDictionary[methodName], module.ModuleObject);
            }

            return GetErrorResponse(ErrorType.MethodNotFound, $"Method {rpcRequest.Method} is not supported", rpcRequest.Id, methodName);
        }

        private JsonRpcResponse Execute(JsonRpcRequest request, string methodName, MethodInfo method, object module)
        {
            var expectedParameters = method.GetParameters();
            var providedParameters = request.Params;
            if (expectedParameters.Length != (providedParameters?.Length ?? 0))
            {
                return GetErrorResponse(ErrorType.InvalidParams, $"Incorrect parameters count, expected: {expectedParameters.Length}, actual: {providedParameters?.Length ?? 0}", request.Id, methodName);
            }

            //prepare parameters
            object[] parameters = null;
            if (expectedParameters.Length > 0)
            {
                parameters = GetParameters(expectedParameters, providedParameters);
                if (parameters == null)
                {
                    return GetErrorResponse(ErrorType.InvalidParams, "Incorrect parameters", request.Id, methodName);
                }
            }

            //execute method
            var result = method.Invoke(module, parameters);
            if (!(result is IResultWrapper resultWrapper))
            {
                _logger.Error($"Method {methodName} execution result does not implement IResultWrapper");
                return GetErrorResponse(ErrorType.InternalError, "Internal error", request.Id, methodName);
            }
            
            if (resultWrapper.GetResult() == null || resultWrapper.GetResult().ResultType == ResultType.Failure)
            {
                _logger.Error($"Error during method: {methodName} execution: {resultWrapper.GetResult()?.Error ?? "no result"}");
                return GetErrorResponse(ErrorType.InternalError, "Internal error", request.Id, methodName);
            }

            //process response
            var data = resultWrapper.GetData();
            if (data is byte[] bytes)
            {
                return GetSuccessResponse(bytes.ToHexString(), request.Id);        
            }
            
            if (!(data is IEnumerable collection) || data is string)
            {
                var json = GetDataObject(data);
                return GetSuccessResponse(json, request.Id);        
            }
            
            var items = new List<object>();
            foreach (var item in collection)
            {
                var jsonItem = GetDataObject(item);
                items.Add(jsonItem);
            }
            
            return GetSuccessResponse(items, request.Id);
        }

        private object GetDataObject(object data)
        {
            return data is IJsonRpcResult rpcResult ? rpcResult.ToJson() : data?.ToString();
        }

        private object[] GetParameters(ParameterInfo[] expectedParameters, string[] providedParameters)
        {
            try
            {
                var executionParameters = new List<object>();
                for (var i = 0; i < providedParameters.Length; i++)
                {
                    var providedParameter = providedParameters[i];
                    var expectedParameter = expectedParameters[i];
                    var paramType = expectedParameter.ParameterType;
                    object executionParam;
                    if (typeof(IJsonRpcRequest).IsAssignableFrom(paramType))
                    {
                        executionParam = Activator.CreateInstance(paramType);
                        ((IJsonRpcRequest)executionParam).FromJson(providedParameter);
                    }
                    else
                    {
                        executionParam = Convert.ChangeType(providedParameter, paramType);
                    }
                    executionParameters.Add(executionParam);
                }
                return executionParameters.ToArray();
            }
            catch (Exception e)
            {
                _logger.Error("Error while parsing parameters", e);
                return null;
            }
        }

        private JsonRpcResponse GetSuccessResponse(object result, BigInteger id)
        {
            var response = new JsonRpcResponse
            {
                Id = id,
                Jsonrpc = _jsonRpcConfig.JsonRpcVersion,
                Result = result,
            };

            return response;
        }

        public JsonRpcResponse GetErrorResponse(ErrorType errorType, string message)
        {
            return GetErrorResponse(errorType, message, 0, null);
        }
        
        private JsonRpcResponse GetErrorResponse(ErrorType errorType, string message, BigInteger id, string methodName)
        {
            _logger.Debug($"Sending error response, method: {methodName ?? "none"}, id: {id}, errorType: {errorType}, message: {message}");
            var response = new JsonRpcResponse
            {
                Jsonrpc = _jsonRpcConfig.JsonRpcVersion,
                Id = id,
                Error = new Error
                {
                    Code = _jsonRpcConfig.ErrorCodes[errorType],
                    Message = message
                }
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

            var module = _moduleProvider.GetAllModules().FirstOrDefault(x => x.MethodDictionary.ContainsKey(methodName));
            if (module == null)
            {
                return (ErrorType.MethodNotFound, $"Method {methodName} is not supported");
            }

            if (_moduleProvider.GetEnabledModules().All(x => x.ModuleType != module.ModuleType))
            {
                return (ErrorType.InvalidRequest, $"{module.ModuleType} Module is disabled");
            }

            return (null, null);
        }
    }
}