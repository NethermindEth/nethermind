using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nevermind.Core;
using Nevermind.JsonRpc.DataModel;
using Nevermind.JsonRpc.Module;
using Newtonsoft.Json;

namespace Nevermind.JsonRpc.Test
{
    [TestClass]
    public class JsonRpcServiceTests
    {
        private IJsonRpcService _jsonRpcService;
        private IConfigurationProvider _configurationProvider;
        private IJsonSerializer _jsonSerializer;

        [TestInitialize]
        public void Initialize()
        {
            _configurationProvider = new ConfigurationProvider();
            _jsonSerializer = new JsonSerializer();
            _jsonRpcService = new JsonRpcService(_configurationProvider, new NetModule(), new EthModule(), new Web3Module(), new ConsoleLogger(), _jsonSerializer);
        }

        [TestMethod]
        public void CorrectRequestTest()
        {
            var requestJson = File.ReadAllText("Data/NetVersionRequest.json");
            var rawResponse = _jsonRpcService.SendRequest(requestJson);

            var request = _jsonSerializer.DeserializeObject<JsonRpcRequest>(requestJson);
            var response = _jsonSerializer.DeserializeObject<JsonRpcResponse>(rawResponse);

            Assert.AreEqual(response.Id, request.Id);
            Assert.AreEqual(response.Result, "1");
            Assert.IsNull(response.Error);
            Assert.IsNull(response.Jsonrpc, _configurationProvider.JsonRpcVersion);
        }

        [TestMethod]
        public void IncorrectRequestTest()
        {
            var requestJson = File.ReadAllText("Data/NetVersionBadMethodRequest.json");
            var rawResponse = _jsonRpcService.SendRequest(requestJson);

            var request = _jsonSerializer.DeserializeObject<JsonRpcRequest>(requestJson);
            var response = _jsonSerializer.DeserializeObject<JsonRpcResponse>(rawResponse);

            Assert.AreEqual(response.Id, request.Id);
            Assert.AreEqual(response.Error.Code, _configurationProvider.ErrorCodes[ErrorType.MethodNotFound]);
            Assert.IsNull(response.Result);
            Assert.AreEqual(response.Jsonrpc, _configurationProvider.JsonRpcVersion);
        }
    }
}
