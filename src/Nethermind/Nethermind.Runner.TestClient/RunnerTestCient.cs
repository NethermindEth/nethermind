using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Core;

namespace Nethermind.Runner.TestClient
{
    public class RunnerTestCient : IRunnerTestCient
    {
        private readonly ILogger _logger;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly HttpClient _client;

        public RunnerTestCient(ILogger logger, IJsonSerializer jsonSerializer)
        {
            _logger = logger;
            _jsonSerializer = jsonSerializer;

            _client = new HttpClient {BaseAddress = new Uri("http://localhost:5000")};
            _client.DefaultRequestHeaders.Accept.Clear();
            _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        public async Task<string> SendEthProtocolVersion()
        {
            try
            {
                var request = GetJsonRequest("eth_protocolVersion", null);
                var response = await _client.PostAsync("", new StringContent(request, Encoding.UTF8, "application/json"));
                var content = await response.Content.ReadAsStringAsync();
                return content;
            }
            catch (Exception e)
            {
                _logger.Error("Error during execution", e);
                return $"Error: {e.Message}";
            }  
        }

        public async Task<string> SendEthGetBlockNumber(string blockNumber, bool returnFullTransactionObjects)
        {
            try
            {
                var request = GetJsonRequest("eth_getBlockByNumber", new object[]{blockNumber, returnFullTransactionObjects});
                var response = await _client.PostAsync("", new StringContent(request, Encoding.UTF8, "application/json"));
                var content = await response.Content.ReadAsStringAsync();
                return content;
            }
            catch (Exception e)
            {
                _logger.Error("Error during execution", e);
                return $"Error: {e.Message}";
            }
        }

        public string SendNetVersion()
        {
            throw new NotImplementedException();
        }

        public string SendWeb3ClientVersion()
        {
            throw new NotImplementedException();
        }

        public string SendWeb3Sha3(string content)
        {
            throw new NotImplementedException();
        }

        public string GetJsonRequest(string method, IEnumerable<object> parameters)
        {
            var request = new
            {
                jsonrpc = "2.0",
                method,
                Params = parameters ?? Enumerable.Empty<object>(),
                id = 67
            };
            return _jsonSerializer.Serialize(request);
        }
    }
}