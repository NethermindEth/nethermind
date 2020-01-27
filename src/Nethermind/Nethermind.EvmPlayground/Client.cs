//  Copyright (c) 2018 Demerzel Solutions Limited
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
using System.Threading.Tasks;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Serialization.Json;

namespace Nethermind.EvmPlayground
{
    public static class Address
    {
        public static byte[] Me = Bytes.FromHexString("7e5f4552091a69125d5dfcb7b8c2659029395bdf");
        public static byte[] A = Bytes.FromHexString("2b5ad5c4795c026514f8317c7a215e218dccd6cf");
        public static byte[] B = Bytes.FromHexString("6813eb9362372eef6200f3b1dbc3f819671cba69");
        public static byte[] C = Bytes.FromHexString("1eff47bc3a10a45d4b230b5d10e37751fe6aa718");
        public static byte[] D = Bytes.FromHexString("e1ab8145f7e55dc933d51a18c793f901a3a0b276");
        public static byte[] E = Bytes.FromHexString("e57bfe9f44b819898f47bf37e5af72a0783e1141");
        public static byte[] F = Bytes.FromHexString("d41c057fd1c78805aac12b0a94a405c0461a6fbb");
        public static byte[] G = Bytes.FromHexString("f1f6619b38a98d6de0800f1defc0a6399eb6d30c");
        public static byte[] H = Bytes.FromHexString("f7edc8fa1ecc32967f827c9043fcae6ba73afa5c");
        public static byte[] I = Bytes.FromHexString("4cceba2d7d2b4fdce4304d3e09a1fea9fbeb1528");
    }

    public class Client
    {
        private readonly IJsonRpcClient _jsonRpcClient;
        private readonly ILogger _logger;

        private IJsonSerializer _serializer = new EthereumJsonSerializer();

        public Client()
            : this(new Uri("http://127.0.0.1:8545"))
        {
        }

        public Client(Uri uri)
            : this(new BasicJsonRpcClient(uri, NullLogger.Instance), NullLogger.Instance)
        {
        }

        internal Client(Uri uri, ILogger logger)
            : this(new BasicJsonRpcClient(uri, logger), logger)
        {
        }

        private Client(IJsonRpcClient jsonRpcClient, ILogger logger)
        {
            _logger = logger;
            _logger.Info("Starting Spaceneth client");
            _jsonRpcClient = jsonRpcClient;
        }

        public async Task<string> SendInit(params byte[] data)
        {
            return await SendTransaction(Address.Me, null, data);
        }

        public async Task<string> GetTrace(params byte[] data)
        {
            return await SendTransaction(Address.Me, null, data);
        }

        public async Task<string> SendTransaction(byte[] sender, byte[] to, params byte[] data)
        {
            Transaction transaction = new Transaction(sender, data);
            string responseJson = await _jsonRpcClient.Post("eth_sendTransaction", transaction);
            if (responseJson.StartsWith("Error:"))
            {
                return responseJson;
            }
            
            JsonRpcResponse response = _serializer.Deserialize<JsonRpcResponse>(responseJson);
            return response.Result;
        }

        public async Task<string> GetTrace(string txHash)
        {
            string responseJson = await _jsonRpcClient.Post("debug_traceTransaction", txHash);
            JsonRpcResponse<GethLikeTxTrace> response = _serializer.Deserialize<JsonRpcResponse<GethLikeTxTrace>>(responseJson);
            return _serializer.Serialize(response.Result, true);
        }

        public async Task<string> GetReceipt(string txHash)
        {
            string responseJson = await _jsonRpcClient.Post("eth_getTransactionReceipt", txHash);
            return responseJson;
        }
    }
}