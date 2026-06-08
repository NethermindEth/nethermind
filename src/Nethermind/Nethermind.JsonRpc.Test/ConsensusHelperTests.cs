// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using Nethermind.Core.Crypto;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Serialization.Json;

using NUnit.Framework;
using Newtonsoft.Json.Linq;

namespace Nethermind.JsonRpc.Test
{
    [Explicit]
    public partial class ConsensusHelperTests
    {
        public static IEnumerable Tests
        {
            get
            {
                // yield return new TestCaseData(new Uri("file:///c:/temp/data1"), new Uri("file:///c:/temp/data1"));
                // yield return new TestCaseData(new Uri("http://localhost:8545"), new Uri("http://localhost:8545"), 10l);
                // yield return new TestCaseData(new Uri("http://localhost:8545"), new Uri("http://localhost:8545"), new Keccak("0x0"), new GethTraceOptions());
                yield break;
            }
        }

        [TestCaseSource(nameof(Tests))]
        public async Task CompareReceipts(Uri uri1, Uri uri2, Hash256? blockHash = null)
        {
            using IConsensusDataSource<IEnumerable<ReceiptForRpc>> receipt1Source = GetSource<IEnumerable<ReceiptForRpc>>(uri1);
            using IConsensusDataSource<IEnumerable<ReceiptForRpc>> receipt2Source = GetSource<IEnumerable<ReceiptForRpc>>(uri2);
            TrySetData(blockHash, receipt1Source, receipt2Source);
            await CompareCollection(receipt1Source, receipt2Source, false);
        }

        [TestCaseSource(nameof(Tests))]
        public async Task CompareReceipt(Uri uri1, Uri uri2, Hash256? blockHash = null)
        {
            using IConsensusDataSource<ReceiptForRpc> receipt1Source = GetSource<ReceiptForRpc>(uri1);
            using IConsensusDataSource<ReceiptForRpc> receipt2Source = GetSource<ReceiptForRpc>(uri2);
            TrySetData(blockHash, receipt1Source, receipt2Source);
            await Compare(receipt1Source, receipt2Source, false);
        }

        [TestCaseSource(nameof(Tests))]
        public async Task CompareGethBlockTrace(Uri uri1, Uri uri2, Hash256? blockHash = null, GethTraceOptions? gethTraceOptions = null)
        {
            gethTraceOptions ??= GethTraceOptions.Default;
            using IConsensusDataSource<IEnumerable<GethLikeTxTrace>> receipt1Source = GetSource<IEnumerable<GethLikeTxTrace>>(uri1);
            using IConsensusDataSource<IEnumerable<GethLikeTxTrace>> receipt2Source = GetSource<IEnumerable<GethLikeTxTrace>>(uri2);
            TrySetData(blockHash, receipt1Source, receipt2Source);
            TrySetData(gethTraceOptions, receipt1Source, receipt2Source);
            await CompareCollection(receipt1Source, receipt2Source, true);
        }

        [TestCaseSource(nameof(Tests))]
        public async Task CompareGethTxTrace(Uri uri1, Uri uri2, Hash256? transactionHash = null, GethTraceOptions? gethTraceOptions = null)
        {
            gethTraceOptions ??= GethTraceOptions.Default;
            using IConsensusDataSource<GethLikeTxTrace> receipt1Source = GetSource<GethLikeTxTrace>(uri1);
            using IConsensusDataSource<GethLikeTxTrace> receipt2Source = GetSource<GethLikeTxTrace>(uri2);
            TrySetData(transactionHash, receipt1Source, receipt2Source);
            TrySetData(gethTraceOptions, receipt1Source, receipt2Source);
            await Compare(receipt1Source, receipt2Source, true);
        }

        [TestCaseSource(nameof(Tests))]
        public async Task CompareParityBlockTrace(Uri uri1, Uri uri2, ulong blockNumber)
        {
            using IConsensusDataSource<IEnumerable<ParityTxTraceFromStore>> receipt1Source = GetSource<IEnumerable<ParityTxTraceFromStore>>(uri1);
            using IConsensusDataSource<IEnumerable<ParityTxTraceFromStore>> receipt2Source = GetSource<IEnumerable<ParityTxTraceFromStore>>(uri2);
            TrySetData(blockNumber, receipt1Source, receipt2Source);
            await CompareCollection(receipt1Source, receipt2Source, true);
        }

        private void TrySetData<TData>(TData blockHash, params object[] sources)
        {
            foreach (object source in sources)
            {
                if (source is IConsensusDataSourceWithParameter<TData> consensusDataSourceWithBlock)
                {
                    consensusDataSourceWithBlock.Parameter = blockHash;
                }
            }
        }

        private IConsensusDataSource<T> GetSource<T>(Uri uri)
        {
            IJsonSerializer serializer = GetSerializer();
            if (uri.IsFile)
            {
                return new FileConsensusDataSource<T>(uri, serializer);
            }
            else if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            {
                Type type = typeof(T);
                if (type == typeof(IEnumerable<ReceiptForRpc>))
                {
                    return (IConsensusDataSource<T>)new ReceiptsJsonRpcDataSource(uri, serializer);
                }
                if (type == typeof(ReceiptForRpc))
                {
                    return (IConsensusDataSource<T>)new ReceiptsJsonRpcDataSource(uri, serializer);
                }
                else if (type == typeof(IEnumerable<GethLikeTxTrace>))
                {
                    return (IConsensusDataSource<T>)new GethLikeBlockTraceJsonRpcDataSource(uri, serializer);
                }
                else if (type == typeof(GethLikeTxTrace))
                {
                    return (IConsensusDataSource<T>)new GethLikeTxTraceJsonRpcDataSource(uri, serializer);
                }
                else if (type == typeof(IEnumerable<ParityTxTraceFromStore>))
                {
                    return (IConsensusDataSource<T>)new ParityLikeBlockTraceJsonRpcDataSource(uri, serializer);
                }
            }

            throw new NotSupportedException($"Uri: {uri} is not supported");
        }

        private IJsonSerializer GetSerializer()
        {
            IJsonSerializer jsonSerializer = new EthereumJsonSerializer();

            return jsonSerializer;
        }

        private static async Task Compare<T>(IConsensusDataSource<T> source1,
            IConsensusDataSource<T> source2,
            bool compareJson)
        {

            if (compareJson)
            {
                string dataJson = await source1.GetJsonData();
                string expectationJson = await source2.GetJsonData();
                JsonNode data = JsonHelper.ParseNormalize(dataJson);
                JsonNode expectation = JsonHelper.ParseNormalize(expectationJson);
                Assert.That(JToken.Parse(data.ToJsonString()), Is.EqualTo(JToken.Parse(expectation.ToJsonString())).Using(JToken.EqualityComparer));

                JsonNode? error = data["error"];
                Assert.That(error, Is.Null, error?.ToString());
            }
            else
            {
                string dataJson = string.Empty, expectationJson = string.Empty;
                try
                {
                    (_, dataJson) = await source1.GetData();
                    (_, expectationJson) = await source2.GetData();
                    JsonNode data = JsonHelper.ParseNormalize(dataJson);
                    JsonNode expectation = JsonHelper.ParseNormalize(expectationJson);
                    Assert.That(JToken.Parse(data.ToJsonString()), Is.EqualTo(JToken.Parse(expectation.ToJsonString())).Using(JToken.EqualityComparer));
                }
                finally
                {
                    await WriteOutJson(dataJson, expectationJson);
                }
            }
        }

        private static async Task CompareCollection<T>(IConsensusDataSource<IEnumerable<T>> source1,
            IConsensusDataSource<IEnumerable<T>> source2,
            bool compareJson)
        {

            if (compareJson)
            {
                string dataJson = await source1.GetJsonData();
                string expectationJson = await source2.GetJsonData();
                JsonNode data = JsonHelper.ParseNormalize(dataJson);
                JsonNode expectation = JsonHelper.ParseNormalize(expectationJson);
                Assert.That(JToken.Parse(data.ToJsonString()), Is.EqualTo(JToken.Parse(expectation.ToJsonString())).Using(JToken.EqualityComparer));

                JsonNode? error = data["error"];
                Assert.That(error, Is.Null, error?.ToString());
            }
            else
            {
                string dataJson = string.Empty, expectationJson = string.Empty;
                try
                {
                    (_, dataJson) = await source1.GetData();
                    (_, expectationJson) = await source2.GetData();
                    JsonNode data = JsonHelper.ParseNormalize(dataJson);
                    JsonNode expectation = JsonHelper.ParseNormalize(expectationJson);
                    Assert.That(JToken.Parse(data.ToJsonString()), Is.EqualTo(JToken.Parse(expectation.ToJsonString())).Using(JToken.EqualityComparer));
                }
                finally
                {
                    await WriteOutJson(dataJson, expectationJson);
                }

            }
        }

        private static async Task WriteOutJson(string dataJson, string expectationJson)
        {
            try
            {
                JsonNode data = JsonHelper.ParseNormalize(dataJson);
                JsonNode expectation = JsonHelper.ParseNormalize(expectationJson);
                await TestContext.Out.WriteLineAsync();
                await TestContext.Out.WriteLineAsync(data.ToString());
                await TestContext.Out.WriteLineAsync();
                await TestContext.Out.WriteLineAsync("-------------------------------------------------------------");
                await TestContext.Out.WriteLineAsync();
                await TestContext.Out.WriteLineAsync(expectation.ToString());
            }
            catch (JsonException) { }
        }

        private interface IConsensusDataSource<T> : IDisposable
        {
            Task<(T, string)> GetData();
            Task<string> GetJsonData();
        }

        private interface IConsensusDataSourceWithParameter<TData>
        {
            TData Parameter { get; set; }
        }

        public static class JsonHelper
        {
            public static JsonNode ParseNormalize(string json) => Normalize(JsonNode.Parse(json));

            public static JsonNode Normalize(JsonNode? token)
            {
                if (token is JsonObject jObject)
                {
                    JsonObject copy = [];
                    foreach (KeyValuePair<string, JsonNode?> prop in jObject)
                    {
                        JsonNode? child = prop.Value;
                        if (child is JsonObject || child is JsonArray)
                        {
                            child = Normalize(child);
                        }
                        if (!IsEmpty(child))
                        {
                            copy.Add(prop.Key, child);
                        }
                    }
                    return copy;
                }
                else if (token is JsonArray jArray)
                {
                    JsonArray copy = [];
                    foreach (JsonNode? item in jArray)
                    {
                        JsonNode? child = item;
                        if (child is JsonObject || child is JsonArray)
                        {
                            child = Normalize(child);
                        }
                        if (!IsEmpty(child))
                        {
                            copy.Add(child);
                        }
                    }
                    return copy;
                }
                return token!;
            }

            public static bool IsEmpty(JsonNode? token) => (token is null);
        }
    }
}
