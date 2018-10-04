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
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.JsonRpc.DataModel;

namespace Nethermind.DiagTools
{
    internal class Program
    {
        private const string NethVm1Uri = "http://94.237.51.104:8345"; // neth vm1

//        private const string Uri = "http://127.0.0.1:8545"; // local
//        private const string Uri = "http://94.237.54.17:8545"; // geth vm4
        private static TxTraceCompare _comparer = new TxTraceCompare();
        private static HttpClient _client = new HttpClient();
        private static IJsonSerializer _serializer = new UnforgivingJsonSerializer();

        public class Result
        {
            public int Gas { get; set; }
        }
        
        public class TX
        {
            public Result result { get; set; }
        }
        
        public static async Task Main(params string[] args)
        {
//            string[] files = Directory.GetFiles("D:\\block_traces\\6108276\\").OrderBy(f => f).ToArray();
//            foreach (var file in files)
//            {
//                string text = File.ReadAllText(file);
//                TX a = _serializer.Deserialize<TX>(text);
//                string txName = Path.GetFileNameWithoutExtension(file);
//                Console.WriteLine(txName.Substring(txName.IndexOf('_') + 1) + ", " + a.result.Gas);
//            }
//
//            Console.ReadLine();
//            return;
            
            
            int blockNumber = 6108276;
            string pathBase = $"D:\\block_traces\\{blockNumber}\\";
            try
            {
                if (!Directory.Exists(pathBase)) Directory.CreateDirectory(pathBase);

                for (int i = 41; i < 42; i++) await TraceTxByBlockhashAndIndex(NethVm1Uri, pathBase, blockNumber, i);
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                throw;
            }
        }

        private static async Task TraceBlock(string uri, string pathBase, int blockNumber)
        {
            HttpRequestMessage msg = new HttpRequestMessage(HttpMethod.Post, uri); // neth vm1
            msg.Content = new StringContent($"{{\"jsonrpc\":\"2.0\",\"method\":\"debug_traceBlockByNumber\",\"params\":[\"{string.Format("0x{0:x}", blockNumber)}\"],\"id\":42}}");
            msg.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            HttpResponseMessage rsp = await _client.SendAsync(msg);
            string text = await rsp.Content.ReadAsStringAsync();
            var nethTrace = _serializer.Deserialize<JsonRpcResponse<BlockTraceItem[]>>(text);

            for (int i = 0; i < nethTrace.Result.Length; i++)
            {
                if (nethTrace.Result[i].Result == null)
                {
                    string nethPath = Path.Combine(pathBase, $"{i}_empty.txt");
                    File.WriteAllText(nethPath, "empty");
                }
                else
                {
                    text = _serializer.Serialize(nethTrace.Result[i].Result, true);
                    string nethPath = Path.Combine(pathBase, $"{i}.txt");
                    File.WriteAllText(nethPath, text);
                }
            }
        }

        private static async Task<TransactionTrace> TraceTx(string uri, string pathBase, string txHash)
        {
            string nethPath = Path.Combine(pathBase, $"{txHash}.txt");
            string text;

            JsonRpcResponse<TransactionTrace> nethTrace;
            if (!File.Exists(nethPath))
            {
                HttpRequestMessage msg = new HttpRequestMessage(HttpMethod.Post, uri);
                msg.Content = new StringContent($"{{\"jsonrpc\":\"2.0\",\"method\":\"debug_traceTransaction\",\"params\":[\"{txHash}\"],\"id\":42}}");
                msg.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                HttpResponseMessage rsp = await _client.SendAsync(msg);
                text = await rsp.Content.ReadAsStringAsync();
                nethTrace = _serializer.Deserialize<JsonRpcResponse<TransactionTrace>>(text);
                text = _serializer.Serialize(nethTrace, true);
                File.WriteAllText(nethPath, text);
            }
            else
            {
                text = File.ReadAllText(nethPath);
                nethTrace = _serializer.Deserialize<JsonRpcResponse<TransactionTrace>>(text);
            }

            return nethTrace.Result;
        }

        
        
        private static async Task<TransactionTrace> TraceTx(string uri, string pathBase, int blockNumber, int txIndex)
        {
            string nethPath = Path.Combine(pathBase, $"{txIndex}.txt");
            string text;

            JsonRpcResponse<TransactionTrace> trace;
            if (!File.Exists(nethPath))
            {
                HttpRequestMessage msg = new HttpRequestMessage(HttpMethod.Post, uri);
                msg.Content = new StringContent($"{{\"jsonrpc\":\"2.0\",\"method\":\"debug_traceTransactionByBlockAndIndex\",\"params\":[\"{string.Format("0x{0:x}", blockNumber)}\", \"{txIndex}\"],\"id\":42}}");
                msg.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                HttpResponseMessage rsp = await _client.SendAsync(msg);
                text = await rsp.Content.ReadAsStringAsync();
                trace = _serializer.Deserialize<JsonRpcResponse<TransactionTrace>>(text);
                text = _serializer.Serialize(trace, true);
                File.WriteAllText(nethPath, text);
            }
            else
            {
                text = File.ReadAllText(nethPath);
                trace = _serializer.Deserialize<JsonRpcResponse<TransactionTrace>>(text);
            }

            return trace.Result;
        }
        
        private static async Task<TransactionTrace> TraceTxByBlockhashAndIndex(string uri, string pathBase, int blockNumber, int txIndex)
        {
            string nethPath = Path.Combine(pathBase, $"{txIndex}.txt");
            string text;

            JsonRpcResponse<TransactionTrace> trace;
            if (!File.Exists(nethPath))
            {
                HttpRequestMessage msg = new HttpRequestMessage(HttpMethod.Post, uri);
                msg.Content = new StringContent($"{{\"jsonrpc\":\"2.0\",\"method\":\"debug_traceTransactionByBlockhashAndIndex\",\"params\":[\"0x27ba6cf216ae4f28c5f15c064033f304d43d9ae5be5e0d12b22539f04a9328f0\", \"{txIndex}\"],\"id\":42}}");
                msg.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                HttpResponseMessage rsp = await _client.SendAsync(msg);
                text = await rsp.Content.ReadAsStringAsync();
                trace = _serializer.Deserialize<JsonRpcResponse<TransactionTrace>>(text);
                text = _serializer.Serialize(trace, true);
                File.WriteAllText(nethPath, text);
            }
            else
            {
                text = File.ReadAllText(nethPath);
                trace = _serializer.Deserialize<JsonRpcResponse<TransactionTrace>>(text);
            }

            return trace.Result;
        }
    }
}