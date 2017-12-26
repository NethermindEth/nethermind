using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Nevermind.Core;
using Nevermind.Json;
using Nevermind.JsonRpc.DataModel;
using Nevermind.JsonRpc.Module;
using Nevermind.Utils.Model;

namespace Nevermind.JsonRpc
{
    public class JsonRpcService : IJsonRpcService
    {
        private readonly ILogger _logger; 
        private readonly IConfigurationProvider _configurationProvider;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly IModuleProvider _moduleProvider;

        public JsonRpcService(IConfigurationProvider configurationProvider, ILogger logger, IJsonSerializer jsonSerializer, IModuleProvider moduleProvider)
        {
            _configurationProvider = configurationProvider;
            _logger = logger;
            _jsonSerializer = jsonSerializer;
            _moduleProvider = moduleProvider;
        }

        public string SendRequest(string request)
        {
            try
            {
                var rpcRequest = _jsonSerializer.DeserializeObject<JsonRpcRequest>(request);
                var validateResult = Validate(rpcRequest);
                if (validateResult.Item1.HasValue)
                {
                    return GetErrorResponse(validateResult.Item1.Value, validateResult.Item2, rpcRequest.Id);
                }
                try
                {
                    return ExecuteRequest(rpcRequest);
                }
                catch (Exception ex)
                {
                    _logger.Error("Error during methodName execution", ex);
                    return GetErrorResponse(ErrorType.InternalError, "Internal error", rpcRequest.Id);
                }
            }
            catch (Exception ex)
            {
                _logger.Error("Error during methodName parsing/validation", ex);
                return GetErrorResponse(ErrorType.ParseError, "Incorrect message", null);
            }         
        }

        private string ExecuteRequest(JsonRpcRequest rpcRequest)
        {
            var methodName = rpcRequest.Method.Trim().ToLower();
            
            var module = _moduleProvider.GetEnabledModules().FirstOrDefault(x => x.MethodDictionary.ContainsKey(methodName));
            if (module != null)
            {
                return Execute(methodName, rpcRequest, module.MethodDictionary, module.ModuleObject);
            }

            return GetErrorResponse(ErrorType.MethodNotFound, $"Method {rpcRequest.Method} is not supported", rpcRequest.Id);
        }

        private string Execute(string methodName, JsonRpcRequest request, IDictionary<string, MethodInfo> methodDict, object module)
        {
            var method = methodDict[methodName];
            var expectedParameters = method.GetParameters();
            var providedParameters = request.Params;
            if (expectedParameters.Length != (providedParameters?.Length ?? 0))
            {
                return GetErrorResponse(ErrorType.InvalidParams, $"Incorrect parameters count, expected: {expectedParameters.Length}", request.Id);
            }

            //prepare parameters
            object[] parameters = null;
            if (expectedParameters.Length > 0)
            {
                parameters = GetParameters(expectedParameters, providedParameters);
                if (parameters == null)
                {
                    return GetErrorResponse(ErrorType.InvalidParams, "Incorrect parameters", request.Id);
                }
            }

            //execute method
            var result = method.Invoke(module, parameters);
            var resultWrapper = result as IResultWrapper;
            if (resultWrapper == null)
            {
                _logger.Error($"Method {methodName} execution result does not implement IResultWrapper");
                return GetErrorResponse(ErrorType.InternalError, "Internal error", request.Id);
            }
            if (resultWrapper.GetResult() == null || resultWrapper.GetResult().ResultType == ResultType.Failure)
            {
                _logger.Error($"Error during method: {methodName} execution: {resultWrapper.GetResult()?.Error ?? "no result"}");
                return GetErrorResponse(ErrorType.InternalError, "Internal error", request.Id);
            }

            //process response
            var data = resultWrapper.GetData();
            var collection = data as IEnumerable;
            if (collection == null || data is string)
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
            return data is IJsonRpcResult rpcResult ? rpcResult.ToJson() : data.ToString();
        }

        private object[] GetParameters(ParameterInfo[] expectedParameters, string[] providedParameters)
        {
            try
            {
                var executionParameters = new List<object>();
                var i = 0;
                foreach (var providedParameter in providedParameters)
                {
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
                    i++;
                }
                return executionParameters.ToArray();
            }
            catch (Exception e)
            {
                _logger.Error("Error while parsing parameters", e);
                return null;
            }
        }

        private string GetSuccessResponse(object result, string id)
        {
            var response = new JsonRpcResponse
            {
                Jsonrpc = _configurationProvider.JsonRpcVersion,
                Id = id,
                Result = result
            };
            return _jsonSerializer.SerializeObject(response);
        }

        private string GetErrorResponse(ErrorType invalidRequest, string message, string id)
        {
            var response = new JsonRpcResponse
            {
                Jsonrpc = _configurationProvider.JsonRpcVersion,
                Id = id,
                Error = new Error
                {
                    Code = _configurationProvider.ErrorCodes[invalidRequest],
                    Message = message
                }
            };
            return _jsonSerializer.SerializeObject(response);
        }

        private (ErrorType?, string) Validate(JsonRpcRequest rpcRequest)
        {
            var methodName = rpcRequest.Method;
            if (string.IsNullOrEmpty(methodName))
            {
                return (ErrorType.InvalidRequest, "Method is required");
            }
            methodName = methodName.Trim().ToLower();

            var module = _moduleProvider.GetAllModules().FirstOrDefault(x => x.MethodDictionary.ContainsKey(methodName));
            if (module == null)
            {
                return (ErrorType.MethodNotFound, "Method is not supported");
            }

            if (_moduleProvider.GetEnabledModules().All(x => x.ModuleType != module.ModuleType))
            {
                return (ErrorType.InvalidRequest, $"{module.ModuleType} Module is disabled");
            }

            return (null, null);
        }
    }
}