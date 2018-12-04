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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Logging;
using Nethermind.Core.Model;
using Nethermind.JsonRpc.Data.Converters;
using Nethermind.JsonRpc.Modules;
using Newtonsoft.Json;
using JsonSerializer = Newtonsoft.Json.JsonSerializer;

namespace Nethermind.JsonRpc
{
    [Todo(Improve.Refactor, "Use JsonConverters and JSON serialization everywhere")]
    public class JsonRpcService : IJsonRpcService
    {
        public static IDictionary<ErrorType, int> ErrorCodes => new Dictionary<ErrorType, int>
        {
            { ErrorType.ParseError, -32700 },
            { ErrorType.InvalidRequest, -32600 },
            { ErrorType.MethodNotFound, -32601 },
            { ErrorType.InvalidParams, -32602 },
            { ErrorType.InternalError, -32603 }
        };
        
        public const string JsonRpcVersion = "2.0";
        
        public static IReadOnlyCollection<JsonConverter> GetStandardConverters()
        {
            return new JsonConverter[]
            {
                new AddressConverter(),
                new KeccakConverter(),
                new BloomConverter(),
                new ByteArrayConverter(),
                new UInt256Converter(),
                new BigIntegerConverter(),
                new NullableBigIntegerConverter()
            };
        }

        private readonly ILogger _logger;
        private readonly IRpcModuleProvider _rpcModuleProvider;
        private readonly JsonSerializer _serializer;

        private Dictionary<Type, JsonConverter> _converterLookup = new Dictionary<Type, JsonConverter>();

        public JsonRpcService(IRpcModuleProvider rpcModuleProvider, ILogManager logManager)
        {
            _logger = logManager.GetClassLogger();
            _rpcModuleProvider = rpcModuleProvider;
            _serializer = new JsonSerializer();

            foreach (ModuleInfo module in _rpcModuleProvider.GetEnabledModules())
            {
                foreach (JsonConverter converter in module.Converters)
                {
                    if (_logger.IsDebug) _logger.Debug($"Registering {converter.GetType().Name}");
                    _serializer.Converters.Add(converter);
                    _converterLookup.Add(converter.GetType().BaseType.GenericTypeArguments[0], converter);
                    Converters.Add(converter);
                }
            }

            foreach (JsonConverter converter in GetStandardConverters())
            {
                if (_logger.IsDebug) _logger.Debug($"Registering {converter.GetType().Name}");
                _serializer.Converters.Add(converter);
                _converterLookup.Add(converter.GetType().BaseType.GenericTypeArguments[0], converter);
                Converters.Add(converter);
            }
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
                    if (_logger.IsError) _logger.Error($"Error during method execution, request: {rpcRequest}", ex.InnerException);
                    return GetErrorResponse(ErrorType.InternalError, "Internal error", rpcRequest.Id, rpcRequest.Method);
                }
                catch (Exception ex)
                {
                    if (_logger.IsError) _logger.Error($"Error during method execution, request: {rpcRequest}", ex);
                    return GetErrorResponse(ErrorType.InternalError, "Internal error", rpcRequest.Id, rpcRequest.Method);
                }
            }
            catch (Exception ex)
            {
                if (_logger.IsError) _logger.Error($"Error during validation, request: {rpcRequest}", ex);
                return GetErrorResponse(ErrorType.ParseError, "Incorrect message", 0, null);
            }
        }

        private JsonRpcResponse ExecuteRequest(JsonRpcRequest rpcRequest)
        {
            var methodName = rpcRequest.Method.Trim().ToLower();

            var module = _rpcModuleProvider.GetEnabledModules().FirstOrDefault(x => x.MethodDictionary.ContainsKey(methodName));
            if (module != null)
            {
                return Execute(rpcRequest, methodName, module.MethodDictionary[methodName], module.ModuleObject);
            }

            return GetErrorResponse(ErrorType.MethodNotFound, $"Method {rpcRequest.Method} is not supported", rpcRequest.Id, methodName);
        }

        private ConcurrentDictionary<RuntimeMethodHandle, ParameterInfo[]> _cachedParameters = new ConcurrentDictionary<RuntimeMethodHandle, ParameterInfo[]>();
        
        private JsonRpcResponse Execute(JsonRpcRequest request, string methodName, MethodInfo method, object module)
        {
            var expectedParameters = _cachedParameters.GetOrAdd(method.MethodHandle, handle => method.GetParameters());
            var providedParameters = request.Params;
            if (expectedParameters.Length != (providedParameters?.Length ?? 0))
            {
                return GetErrorResponse(ErrorType.InvalidParams, $"Incorrect parameters count, expected: {expectedParameters.Length}, actual: {providedParameters?.Length ?? 0}", request.Id, methodName);
            }

            //prepare parameters
            object[] parameters = null;
            if (expectedParameters.Length > 0)
            {
                parameters = DeserializeParameters(expectedParameters, providedParameters);
                if (parameters == null)
                {
                    return GetErrorResponse(ErrorType.InvalidParams, "Incorrect parameters", request.Id, methodName);
                }
            }

            //execute method
            if (!(method.Invoke(module, parameters) is IResultWrapper resultWrapper))
            {
                if (_logger.IsError) _logger.Error($"Method {methodName} execution result does not implement IResultWrapper");
                return GetErrorResponse(ErrorType.InternalError, "Internal error", request.Id, methodName);
            }

            Result result = resultWrapper.GetResult();            
            if (result == null || result.ResultType == ResultType.Failure)
            {
                if (_logger.IsError) _logger.Error($"Error during method: {methodName} execution: {result?.Error ?? "no result"}");
                return GetErrorResponse(ErrorType.InternalError, "Internal error", request.Id, methodName);
            }

            return GetSuccessResponse(resultWrapper.GetData(), request.Id);
        }

        private object[] DeserializeParameters(ParameterInfo[] expectedParameters, string[] providedParameters)
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
                        ((IJsonRpcRequest) executionParam).FromJson(providedParameter);
                    }
                    else if (paramType == typeof(string[]))
                    {
                        executionParam = _serializer.Deserialize<string[]>(new JsonTextReader(new StringReader(providedParameter)));
                    }
                    else
                    {

                        executionParam = JsonConvert.DeserializeObject($"\"{providedParameter}\"", paramType, Converters.ToArray());
                    }

                    executionParameters.Add(executionParam);
                }

                return executionParameters.ToArray();
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error("Error while parsing parameters", e);
                return null;
            }
        }

        private JsonRpcResponse GetSuccessResponse(object result, ulong id)
        {
            var response = new JsonRpcResponse
            {
                Id = id,
                JsonRpc = JsonRpcVersion,
                Result = result,
            };

            return response;
        }

        public JsonRpcResponse GetErrorResponse(ErrorType errorType, string message)
        {
            return GetErrorResponse(errorType, message, 0, null);
        }

        public IList<JsonConverter> Converters { get; } = new List<JsonConverter>();

        private JsonRpcResponse GetErrorResponse(ErrorType errorType, string message, ulong id, string methodName)
        {
            if (_logger.IsDebug) _logger.Debug($"Sending error response, method: {methodName ?? "none"}, id: {id}, errorType: {errorType}, message: {message}");
            var response = new JsonRpcResponse
            {
                JsonRpc = JsonRpcVersion,
                Id = id,
                Error = new Error
                {
                    Code = ErrorCodes[errorType],
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

            var module = _rpcModuleProvider.GetAllModules().FirstOrDefault(x => x.MethodDictionary.ContainsKey(methodName));
            if (module == null)
            {
                return (ErrorType.MethodNotFound, $"Method {methodName} is not supported");
            }

            if (_rpcModuleProvider.GetEnabledModules().All(x => x.ModuleType != module.ModuleType))
            {
                return (ErrorType.InvalidRequest, $"{module.ModuleType} module is disabled");
            }

            return (null, null);
        }
    }
}