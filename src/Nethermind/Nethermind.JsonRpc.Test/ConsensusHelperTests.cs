// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Equivalency;
using FluentAssertions.Json;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Serialization.Json;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test
{
    [Explicit]
    public partial class ConsensusHelperTests
    {
        private static Func<EquivalencyAssertionOptions<ReceiptForRpc>, EquivalencyAssertionOptions<ReceiptForRpc>> ReceiptOptions =
            options => options.WithStrictOrdering()
                .IncludingNestedObjects()
                .Including(r => r.TransactionHash)
                .Including(r => r.TransactionIndex)
                .Including(r => r.BlockHash)
                .Including(r => r.BlockNumber)
                .Including(r => r.From)
                .Including(r => r.To)
                .Including(r => r.CumulativeGasUsed)
                .Including(r => r.GasUsed)
                .Including(r => r.ContractAddress)
                .Including(r => r.LogsBloom)
                .Including(r => r.Logs)
                .Including(r => r.Root)
                .Including(r => r.Status);

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
        public async Task CompareReceipts(Uri uri1, Uri uri2, Keccak? blockHash = null)
        {
            using IConsensusDataSource<IEnumerable<ReceiptForRpc>> receipt1Source = GetSource<IEnumerable<ReceiptForRpc>>(uri1);
            using IConsensusDataSource<IEnumerable<ReceiptForRpc>> receipt2Source = GetSource<IEnumerable<ReceiptForRpc>>(uri2);
            TrySetData(blockHash, receipt1Source, receipt2Source);
            await CompareCollection(receipt1Source, receipt2Source, false, ReceiptOptions);
        }

        [TestCaseSource(nameof(Tests))]
        public async Task CompareReceipt(Uri uri1, Uri uri2, Keccak? blockHash = null)
        {
            using IConsensusDataSource<ReceiptForRpc> receipt1Source = GetSource<ReceiptForRpc>(uri1);
            using IConsensusDataSource<ReceiptForRpc> receipt2Source = GetSource<ReceiptForRpc>(uri2);
            TrySetData(blockHash, receipt1Source, receipt2Source);
            await Compare(receipt1Source, receipt2Source, false, ReceiptOptions);
        }

        [TestCaseSource(nameof(Tests))]
        public async Task CompareGethBlockTrace(Uri uri1, Uri uri2, Keccak? blockHash = null, GethTraceOptions? gethTraceOptions = null)
        {
            gethTraceOptions ??= GethTraceOptions.Default;
            using IConsensusDataSource<IEnumerable<GethLikeTxTrace>> receipt1Source = GetSource<IEnumerable<GethLikeTxTrace>>(uri1, DebugModuleFactory.Converters);
            using IConsensusDataSource<IEnumerable<GethLikeTxTrace>> receipt2Source = GetSource<IEnumerable<GethLikeTxTrace>>(uri2, DebugModuleFactory.Converters);
            TrySetData(blockHash, receipt1Source, receipt2Source);
            TrySetData(gethTraceOptions, receipt1Source, receipt2Source);
            await CompareCollection(receipt1Source, receipt2Source, true);
        }

        [TestCaseSource(nameof(Tests))]
        public async Task CompareGethTxTrace(Uri uri1, Uri uri2, Keccak? transactionHash = null, GethTraceOptions? gethTraceOptions = null)
        {
            gethTraceOptions ??= GethTraceOptions.Default;
            using IConsensusDataSource<GethLikeTxTrace> receipt1Source = GetSource<GethLikeTxTrace>(uri1, DebugModuleFactory.Converters);
            using IConsensusDataSource<GethLikeTxTrace> receipt2Source = GetSource<GethLikeTxTrace>(uri2, DebugModuleFactory.Converters);
            TrySetData(transactionHash, receipt1Source, receipt2Source);
            TrySetData(gethTraceOptions, receipt1Source, receipt2Source);
            await Compare(receipt1Source, receipt2Source, true);
        }

        [TestCaseSource(nameof(Tests))]
        public async Task CompareParityBlockTrace(Uri uri1, Uri uri2, long blockNumber)
        {
            using IConsensusDataSource<IEnumerable<ParityTxTraceFromStore>> receipt1Source = GetSource<IEnumerable<ParityTxTraceFromStore>>(uri1, TraceModuleFactory.Converters);
            using IConsensusDataSource<IEnumerable<ParityTxTraceFromStore>> receipt2Source = GetSource<IEnumerable<ParityTxTraceFromStore>>(uri2, TraceModuleFactory.Converters);
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

        private IConsensusDataSource<T> GetSource<T>(Uri uri, IEnumerable<JsonConverter>? additionalConverters = null)
        {
            var serializer = GetSerializer(additionalConverters!);
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

        private IJsonSerializer GetSerializer(IEnumerable<JsonConverter>? additionalConverters)
        {
            IJsonSerializer jsonSerializer = new EthereumJsonSerializer();
            if (additionalConverters is not null)
            {
                jsonSerializer.RegisterConverters(additionalConverters);
            }

            return jsonSerializer;
        }

        private static async Task Compare<T>(IConsensusDataSource<T> source1,
            IConsensusDataSource<T> source2,
            bool compareJson,
            Func<EquivalencyAssertionOptions<T>, EquivalencyAssertionOptions<T>>? options = null)
        {

            if (compareJson)
            {
                JToken data = JsonHelper.ParseNormalize(await source1.GetJsonData());
                JToken expectation = JsonHelper.ParseNormalize(await source2.GetJsonData());
                data.Should().BeEquivalentTo(expectation);
                data["error"].Should().BeNull(data["error"]?.ToString());
            }
            else
            {
                string dataJson = string.Empty, expectationJson = string.Empty;
                try
                {
                    T data, expectation;
                    (data, dataJson) = await source1.GetData();
                    (expectation, expectationJson) = await source2.GetData();
                    data.Should().BeEquivalentTo(expectation, options ?? (o => o));
                }
                finally
                {
                    await WriteOutJson(dataJson, expectationJson);
                }
            }
        }

        private static async Task CompareCollection<T>(IConsensusDataSource<IEnumerable<T>> source1,
            IConsensusDataSource<IEnumerable<T>> source2,
            bool compareJson,
            Func<EquivalencyAssertionOptions<T>, EquivalencyAssertionOptions<T>>? options = null)
        {

            if (compareJson)
            {
                JToken data = JsonHelper.ParseNormalize(await source1.GetJsonData());
                JToken expectation = JsonHelper.ParseNormalize(await source2.GetJsonData());
                data.Should().BeEquivalentTo(expectation);
                data["error"].Should().BeNull(data["error"]?.ToString());
            }
            else
            {
                string dataJson = string.Empty, expectationJson = string.Empty;
                try
                {
                    IEnumerable<T> data, expectation;
                    (data, dataJson) = await source1.GetData();
                    (expectation, expectationJson) = await source2.GetData();
                    data.Should().BeEquivalentTo(expectation, options ?? (o => o));
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
                JToken data = JsonHelper.ParseNormalize(dataJson);
                JToken expectation = JsonHelper.ParseNormalize(expectationJson);
                await TestContext.Out.WriteLineAsync();
                await TestContext.Out.WriteLineAsync(data.ToString(Formatting.Indented));
                await TestContext.Out.WriteLineAsync();
                await TestContext.Out.WriteLineAsync("-------------------------------------------------------------");
                await TestContext.Out.WriteLineAsync();
                await TestContext.Out.WriteLineAsync(expectation.ToString(Formatting.Indented));
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
            public static JToken ParseNormalize(string json) => Normalize(JToken.Parse(json));

            public static JToken Normalize(JToken token)
            {
                if (token.Type == JTokenType.Object)
                {
                    JObject copy = new();
                    foreach (JProperty prop in token.Children<JProperty>())
                    {
                        JToken child = prop.Value;
                        if (child.HasValues)
                        {
                            child = Normalize(child);
                        }
                        if (!IsEmpty(child))
                        {
                            copy.Add(prop.Name, child);
                        }
                    }
                    return copy;
                }
                else if (token.Type == JTokenType.Array)
                {
                    JArray copy = new();
                    foreach (JToken item in token.Children())
                    {
                        JToken child = item;
                        if (child.HasValues)
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
                return token;
            }

            public static bool IsEmpty(JToken token)
            {
                return (token.Type == JTokenType.Null);
            }
        }
    }
}
