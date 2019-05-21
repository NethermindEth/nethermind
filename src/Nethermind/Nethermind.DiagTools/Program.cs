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
using Nethermind.Core.Json;
using Nethermind.Evm.Tracing;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Client;
using Nethermind.Logging;

namespace Nethermind.DiagTools
{
    internal class Program
    {
        private static TxTraceCompare _comparer = new TxTraceCompare();
        private static HttpClient _client = new HttpClient();
        private static IJsonSerializer _serializer = new UnforgivingJsonSerializer();

        public static async Task Main(params string[] args)
        {
//            int blockNumber = 6108276;
//            string hash = "?";

            int blockNumber = 316658;
            string hash = "0x846806c1e1fc530399ad80cec3121b9d90fe24e4665484b995237f7f95e2caa8";
//            Console.WriteLine("Block Number:");
            // int blockNumber = int.Parse(Console.ReadLine());

            string nethPathBase = $"D:\\block_traces\\{blockNumber}\\neth\\";
            if (!Directory.Exists(nethPathBase)) Directory.CreateDirectory(nethPathBase);

            string gethPathBase = $"D:\\block_traces\\{blockNumber}\\geth\\";
            if (!Directory.Exists(gethPathBase)) Directory.CreateDirectory(gethPathBase);

            BasicJsonRpcClient localhostClient = new BasicJsonRpcClient(KnownRpcUris.Localhost, _serializer, NullLogManager.Instance);
//            await TraceBlock(localhostClient, blockNumber, nethPathBase);
            await TraceBlockByHash(localhostClient, hash, nethPathBase);

            BasicJsonRpcClient gethClient = new BasicJsonRpcClient(KnownRpcUris.Localhost, _serializer, NullLogManager.Instance);
//            await TraceBlock(gethClient, blockNumber, gethPathBase);
            await TraceBlockByHash(gethClient, hash, gethPathBase);

            string nethTx = File.ReadAllText(Path.Combine(nethPathBase, "0.txt"));
            string gethTx = File.ReadAllText(Path.Combine(gethPathBase, "0.txt"));

            GethLikeTxTrace gethTrace = _serializer.Deserialize<GethLikeTxTrace>(gethTx);
            GethLikeTxTrace nethTrace = _serializer.Deserialize<GethLikeTxTrace>(nethTx);
            
            _comparer.Compare(gethTrace, nethTrace);

//            try
//            {
//                for (int i = 41; i < 42; i++) await TraceTxByBlockhashAndIndex(KnownRpcUris.Localhost, pathBase, blockNumber, i);
//            }
//            catch (Exception e)
//            {
//                Console.WriteLine(e);
//                throw;
//            }

            Console.WriteLine("Press any key to exit.");
            Console.ReadKey();
        }

        private static async Task TraceBlock(BasicJsonRpcClient localhostClient, int blockNumber, string pathBase)
        {
            string response = await localhostClient.Post("debug_traceBlockByNumber", string.Format("0x{0:x}", blockNumber));
            var blockTraceItems = _serializer.Deserialize<JsonRpcResponse<GethLikeTxTrace[]>>(response);
            if (blockTraceItems == null)
            {
                return;
            }

            for (int i = 0; i < blockTraceItems.Result.Length; i++)
            {
                if (blockTraceItems.Result[i].ReturnValue == null)
                {
                    string nethPath = Path.Combine(pathBase, $"{i}_empty.txt");
                    File.WriteAllText(nethPath, "empty");
                }
                else
                {
                    string singleTxJson = _serializer.Serialize(blockTraceItems.Result[i].ReturnValue, true);
                    string txPath = Path.Combine(pathBase, $"{i}.txt");
                    File.WriteAllText(txPath, singleTxJson);
                }
            }
        }

        private static async Task TraceBlockByHash(BasicJsonRpcClient localhostClient, string hash, string pathBase)
        {
            string response = await localhostClient.Post("debug_traceBlockByHash", hash);
            var blockTraceItems = _serializer.Deserialize<JsonRpcResponse<GethLikeTxTrace[]>>(response);
            if (blockTraceItems.Error != null)
            {
                return;
            }

            for (int i = 0; i < blockTraceItems.Result.Length; i++)
            {
                if (blockTraceItems.Result[i].ReturnValue == null)
                {
                    string nethPath = Path.Combine(pathBase, $"{i}_empty.txt");
                    File.WriteAllText(nethPath, "empty");
                }
                else
                {
                    string singleTxJson = _serializer.Serialize(blockTraceItems.Result[i].ReturnValue, true);
                    string txPath = Path.Combine(pathBase, $"{i}.txt");
                    File.WriteAllText(txPath, singleTxJson);
                }
            }
        }

        private static async Task<GethLikeTxTrace> TraceTx(string uri, string pathBase, string txHash)
        {
            string nethPath = Path.Combine(pathBase, $"{txHash}.txt");
            string text;

            JsonRpcResponse<GethLikeTxTrace> nethTrace;
            if (!File.Exists(nethPath))
            {
                HttpRequestMessage msg = new HttpRequestMessage(HttpMethod.Post, uri);
                msg.Content = new StringContent($"{{\"jsonrpc\":\"2.0\",\"method\":\"debug_traceTransaction\",\"params\":[\"{txHash}\"],\"id\":42}}");
                msg.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                HttpResponseMessage rsp = await _client.SendAsync(msg);
                text = await rsp.Content.ReadAsStringAsync();
                nethTrace = _serializer.Deserialize<JsonRpcResponse<GethLikeTxTrace>>(text);
                text = _serializer.Serialize(nethTrace, true);
                File.WriteAllText(nethPath, text);
            }
            else
            {
                text = File.ReadAllText(nethPath);
                nethTrace = _serializer.Deserialize<JsonRpcResponse<GethLikeTxTrace>>(text);
            }

            return nethTrace.Result;
        }

        private static async Task<GethLikeTxTrace> TraceTx(string uri, string pathBase, int blockNumber, int txIndex)
        {
            string nethPath = Path.Combine(pathBase, $"{txIndex}.txt");
            string text;

            JsonRpcResponse<GethLikeTxTrace> trace;
            if (!File.Exists(nethPath))
            {
                HttpRequestMessage msg = new HttpRequestMessage(HttpMethod.Post, uri);
                msg.Content = new StringContent($"{{\"jsonrpc\":\"2.0\",\"method\":\"debug_traceTransactionByBlockAndIndex\",\"params\":[\"{string.Format("0x{0:x}", blockNumber)}\", \"{txIndex}\"],\"id\":42}}");
                msg.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                HttpResponseMessage rsp = await _client.SendAsync(msg);
                text = await rsp.Content.ReadAsStringAsync();
                trace = _serializer.Deserialize<JsonRpcResponse<GethLikeTxTrace>>(text);
                text = _serializer.Serialize(trace, true);
                File.WriteAllText(nethPath, text);
            }
            else
            {
                text = File.ReadAllText(nethPath);
                trace = _serializer.Deserialize<JsonRpcResponse<GethLikeTxTrace>>(text);
            }

            return trace.Result;
        }

        private static async Task<GethLikeTxTrace> TraceTxByBlockhashAndIndex(Uri uri, string pathBase, int blockNumber, int txIndex)
        {
            string nethPath = Path.Combine(pathBase, $"{txIndex}.txt");
            string text;

            JsonRpcResponse<GethLikeTxTrace> trace;
            if (!File.Exists(nethPath))
            {
                HttpRequestMessage msg = new HttpRequestMessage(HttpMethod.Post, uri);
                msg.Content = new StringContent($"{{\"jsonrpc\":\"2.0\",\"method\":\"debug_traceTransactionByBlockhashAndIndex\",\"params\":[\"0x27ba6cf216ae4f28c5f15c064033f304d43d9ae5be5e0d12b22539f04a9328f0\", \"{txIndex}\"],\"id\":42}}");
                msg.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                HttpResponseMessage rsp = await _client.SendAsync(msg);
                text = await rsp.Content.ReadAsStringAsync();
                trace = _serializer.Deserialize<JsonRpcResponse<GethLikeTxTrace>>(text);
                text = _serializer.Serialize(trace, true);
                File.WriteAllText(nethPath, text);
            }
            else
            {
                text = File.ReadAllText(nethPath);
                trace = _serializer.Deserialize<JsonRpcResponse<GethLikeTxTrace>>(text);
            }

            return trace.Result;
        }
    }
}