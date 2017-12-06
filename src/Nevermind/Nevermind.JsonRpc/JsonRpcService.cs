using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Nevermind.Core;
using Nevermind.JsonRpc.DataModel;
using Nevermind.JsonRpc.Module;
using Nevermind.Utils;
using Newtonsoft.Json;

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

        public JsonRpcService(IConfigurationProvider configurationProvider, INetModule netModule, IEthModule ethModule, IWeb3Module web3Module, ILogger logger, IJsonSerializer jsonSerializer)
        {
            _configurationProvider = configurationProvider;
            _netModule = netModule;
            _ethModule = ethModule;
            _web3Module = web3Module;
            _logger = logger;
            _jsonSerializer = jsonSerializer;
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
                    _logger.Log($"Error during method execution: {ex}");
                    return GetErrorResponse(ErrorType.InternalError, "Internal error", rpcRequest.Id);
                }
            }
            catch (Exception ex)
            {
                _logger.Log($"Error during method parsing/validation: {ex}");
                return GetErrorResponse(ErrorType.ParseError, "Incorrect message", null);
            }         
        }

        private string ExecuteRequest(JsonRpcRequest rpcRequest)
        {
            if (rpcRequest.Method.CompareIgnoreCase("net_version"))
            {
                var result = _netModule.net_version();
                return GetSuccessResponse(rpcRequest.Id, result);
            }
            if (rpcRequest.Method.CompareIgnoreCase("eth_getBalance"))
            {
                var paramsRaw = rpcRequest.Params;
                if (paramsRaw == null || paramsRaw.Length != 2)
                {
                    return GetErrorResponse(ErrorType.InvalidParams, $"Incorrect parameters for method: {rpcRequest.Method}", rpcRequest.Id);
                }
                var data = new Data();
                data.FromJson(paramsRaw[0]);
                var blockParameter = new BlockParameter();
                blockParameter.FromJson(paramsRaw[1]);

                var result = _ethModule.eth_getBalance(data, blockParameter);
                if (result == null)
                {
                    return GetErrorResponse(ErrorType.ServerError, "Error during method execution", rpcRequest.Id);
                }
                var balanceResult = result.ToJson();
                return GetSuccessResponse(rpcRequest.Id, balanceResult);
            }
            return GetErrorResponse(ErrorType.MethodNotFound, $"Method {rpcRequest.Method} is not supported", rpcRequest.Id);
            
            
            //var type = _netModule.GetType();
            //var method = type.GetMethod("net_version");
            //var methodParameters = method.GetParameters();
            //var parametersRaw = rpcRequest.Params;
            //if (string.IsNullOrEmpty(parametersRaw))
            //{
                
            //}

            //foreach (var parameterInfo in parameters)
            //{
            //    var paramType = parameterInfo.ParameterType;
            //    if (paramType.IsSubclassOf(typeof(IJsonRpcRequest)))
            //    {
            //        var requestParam = (IJsonRpcResult)Activator.CreateInstance(paramType, null);
            //    }
            //}
        }

        private string GetSuccessResponse(string id, object result)
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
            return null;
        }
    }
}