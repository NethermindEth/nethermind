using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Nevermind.Core;
using Nevermind.Json;
using Nevermind.JsonRpc.DataModel;
using Nevermind.JsonRpc.Module;
using Nevermind.Utils;
using Nevermind.Utils.Model;

namespace Nevermind.JsonRpc
{
    public class JsonRpcService : IJsonRpcService
    {
        private readonly ILogger _logger; 
        private readonly IConfigurationProvider _configurationProvider;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly INetModule _netModule;
        private readonly IEthModule _ethModule;
        private readonly IWeb3Module _web3Module;

        private IDictionary<string, MethodInfo> _netMethodDict;
        private IDictionary<string, MethodInfo> _web3MethodDict;
        private IDictionary<string, MethodInfo> _ethMethodDict;

        public JsonRpcService(IConfigurationProvider configurationProvider, INetModule netModule, IEthModule ethModule, IWeb3Module web3Module, ILogger logger, IJsonSerializer jsonSerializer)
        {
            _configurationProvider = configurationProvider;
            _netModule = netModule;
            _ethModule = ethModule;
            _web3Module = web3Module;
            _logger = logger;
            _jsonSerializer = jsonSerializer;

            Initialize();
        }

        public string SendRequest(string request)
        {
            try
            {
                var rpcRequest = _jsonSerializer.DeserializeObject<JsonRpcRequest>(request);
                var validateResults = Validate(rpcRequest);
                if (validateResults != null && validateResults.Any())
                {
                    return GetErrorResponse(ErrorType.InvalidRequest, string.Join(", ", validateResults), rpcRequest.Id);
                }
                try
                {
                    return ExecuteRequest(rpcRequest);
                }
                catch (Exception ex)
                {
                    _logger.Log($"Error during methodName execution: {ex}");
                    return GetErrorResponse(ErrorType.InternalError, "Internal error", rpcRequest.Id);
                }
            }
            catch (Exception ex)
            {
                _logger.Log($"Error during methodName parsing/validation: {ex}");
                return GetErrorResponse(ErrorType.ParseError, "Incorrect message", null);
            }         
        }

        private string ExecuteRequest(JsonRpcRequest rpcRequest)
        {
            var methodName = rpcRequest.Method.Trim().ToLower();
            
            if (_netMethodDict.ContainsKey(methodName))
            {
                return Execute(methodName, rpcRequest, _netMethodDict, _netModule);
            }

            if (_web3MethodDict.ContainsKey(methodName))
            {
                return Execute(methodName, rpcRequest, _web3MethodDict, _web3Module);
            }

            if (_ethMethodDict.ContainsKey(methodName))
            {
                return Execute(methodName, rpcRequest, _ethMethodDict, _ethModule);
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

        private void Initialize()
        {
            _netMethodDict = GetMethodDict(_netModule.GetType());
            _web3MethodDict = GetMethodDict(_web3Module.GetType());
            _ethMethodDict = GetMethodDict(_ethModule.GetType());
        }

        private IDictionary<string, MethodInfo> GetMethodDict(Type type)
        {
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            return methods.ToDictionary(x => x.Name.Trim().ToLower());
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

        private string[] Validate(JsonRpcRequest rpcRequest)
        {
            var methodName = rpcRequest.Method;
            if (string.IsNullOrEmpty(methodName))
            {
                return new[] { "Method is required" };
            }
            methodName = methodName.Trim().ToLower();

            if (_netMethodDict.ContainsKey(methodName) && !_configurationProvider.EnabledModules.Contains(ModuleType.Net))
            {
                return new[] { "Net Module is disabled" };
            }
            if (_web3MethodDict.ContainsKey(methodName) && !_configurationProvider.EnabledModules.Contains(ModuleType.Web3))
            {
                return new[] { "Web3 Module is disabled" };
            }
            if (_ethMethodDict.ContainsKey(methodName) && !_configurationProvider.EnabledModules.Contains(ModuleType.Eth))
            {
                return new[] { "Eth Module is disabled" };
            }
            return null;
        }
    }
}