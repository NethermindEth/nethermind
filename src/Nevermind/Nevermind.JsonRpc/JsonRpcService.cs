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
                (ErrorType?, string) validateResult = Validate(rpcRequest);
                if (validateResult.Item1.HasValue)
                {
                    return GetErrorResponse(validateResult.Item1.Value, validateResult.Item2, rpcRequest?.Id, rpcRequest?.Method);
                }
                try
                {
                    return ExecuteRequest(rpcRequest);
                }
                catch (Exception ex)
                {
                    _logger.Error($"Error during method execution, request: {request}", ex);
                    return GetErrorResponse(ErrorType.InternalError, "Internal error", rpcRequest?.Id, rpcRequest?.Method);
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Error during parsing/validation, request: {request}", ex);
                return GetErrorResponse(ErrorType.ParseError, "Incorrect message", null, null);
            }         
        }

        private string ExecuteRequest(JsonRpcRequest rpcRequest)
        {
            var methodName = rpcRequest.Method.Trim().ToLower();
            
            var module = _moduleProvider.GetEnabledModules().FirstOrDefault(x => x.MethodDictionary.ContainsKey(methodName));
            if (module != null)
            {
                return Execute(rpcRequest, methodName, module.MethodDictionary[methodName], module.ModuleObject);
            }

            return GetErrorResponse(ErrorType.MethodNotFound, $"Method {rpcRequest.Method} is not supported", rpcRequest.Id, methodName);
        }

        private string Execute(JsonRpcRequest request, string methodName, MethodInfo method, object module)
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
            var resultWrapper = result as IResultWrapper;
            if (resultWrapper == null)
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
            var collection = data as IEnumerable;
            if (collection == null || data is string)
            {
                var json = GetDataObject(data);
                return GetSuccessResponse(json, request.Id, methodName);        
            }
            var items = new List<object>();
            foreach (var item in collection)
            {
                var jsonItem = GetDataObject(item);
                items.Add(jsonItem);
            }
            return GetSuccessResponse(items, request.Id, methodName);
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

        private string GetSuccessResponse(object result, string id, string methodName)
        {
            var response = new JsonRpcResponse
            {
                Jsonrpc = _configurationProvider.JsonRpcVersion,
                Id = id,
                Result = result
            };
            var serializedReponse = _jsonSerializer.SerializeObject(response);

            _logger.Debug($"Successfull request processing, method: {methodName ?? "none"}, id: {id ?? "none"}, result: {serializedReponse}");

            return serializedReponse;
        }

        private string GetErrorResponse(ErrorType errorType, string message, string id, string methodName)
        {
            _logger.Error($"Error during processing the request, method: {methodName ?? "none"}, id: {id ?? "none"}, errorType: {errorType}, message: {message}");

            var response = new JsonRpcResponse
            {
                Jsonrpc = _configurationProvider.JsonRpcVersion,
                Id = id,
                Error = new Error
                {
                    Code = _configurationProvider.ErrorCodes[errorType],
                    Message = message
                }
            };
            return _jsonSerializer.SerializeObject(response);
        }

        private (ErrorType?, string) Validate(JsonRpcRequest rpcRequest)
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