//  Copyright (c) 2021 Demerzel Solutions Limited
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
// 

using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Equivalency;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Serialization.Json;
using Newtonsoft.Json;
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
                // yield return new TestCaseData("file:///c:/temp/data1", "file:///c:/temp/data1");
                // yield return new TestCaseData("http://localhost:8545", "http://localhost:8545", new Keccak("0x0"));
                // yield return new TestCaseData("http://localhost:8545", "http://localhost:8545", new Keccak("0x0"), new GethTraceOptions());
                // yield return new TestCaseData("https://localhost:8545", "https://localhost:8545", 1000l);
                yield break;
            }
        }
        
        [TestCaseSource(nameof(Tests))]
        public async Task CompareReceipts(Uri uri1, Uri uri2, Keccak blockHash = null)
        {
            using IConsensusDataSource<IEnumerable<ReceiptForRpc>> receipt1Source = GetSource<IEnumerable<ReceiptForRpc>>(uri1);
            using IConsensusDataSource<IEnumerable<ReceiptForRpc>> receipt2Source = GetSource<IEnumerable<ReceiptForRpc>>(uri2);
            TrySetData(blockHash, receipt1Source, receipt2Source);
            await CompareCollection(receipt1Source, receipt2Source, ReceiptOptions);
        }
        
        [TestCaseSource(nameof(Tests))]
        public async Task CompareReceipt(Uri uri1, Uri uri2, Keccak blockHash = null)
        {
            using IConsensusDataSource<ReceiptForRpc> receipt1Source = GetSource<ReceiptForRpc>(uri1);
            using IConsensusDataSource<ReceiptForRpc> receipt2Source = GetSource<ReceiptForRpc>(uri2);
            TrySetData(blockHash, receipt1Source, receipt2Source);
            await Compare(receipt1Source, receipt2Source, ReceiptOptions);
        }
        
        [TestCaseSource(nameof(Tests))]
        public async Task CompareGethBlockTrace(Uri uri1, Uri uri2, Keccak blockHash = null, GethTraceOptions gethTraceOptions = null)
        {
            gethTraceOptions ??= GethTraceOptions.Default;
            using IConsensusDataSource<IEnumerable<GethLikeTxTrace>> receipt1Source = GetSource<IEnumerable<GethLikeTxTrace>>(uri1, DebugModuleFactory.Converters);
            using IConsensusDataSource<IEnumerable<GethLikeTxTrace>> receipt2Source = GetSource<IEnumerable<GethLikeTxTrace>>(uri2, DebugModuleFactory.Converters);
            TrySetData(blockHash, receipt1Source, receipt2Source);
            TrySetData(gethTraceOptions, receipt1Source, receipt2Source);
            await CompareCollection(receipt1Source, receipt2Source);
        }
        
        [TestCaseSource(nameof(Tests))]
        public async Task CompareGethTxTrace(Uri uri1, Uri uri2, Keccak transactionHash = null, GethTraceOptions gethTraceOptions = null)
        {
            gethTraceOptions ??= GethTraceOptions.Default;
            using IConsensusDataSource<GethLikeTxTrace> receipt1Source = GetSource<GethLikeTxTrace>(uri1, DebugModuleFactory.Converters);
            using IConsensusDataSource<GethLikeTxTrace> receipt2Source = GetSource<GethLikeTxTrace>(uri2, DebugModuleFactory.Converters);
            TrySetData(transactionHash, receipt1Source, receipt2Source);
            TrySetData(gethTraceOptions, receipt1Source, receipt2Source);
            await Compare(receipt1Source, receipt2Source);
        }
        
        [TestCaseSource(nameof(Tests))]
        public async Task CompareParityBlockTrace(Uri uri1, Uri uri2, long blockNumber)
        {
            using IConsensusDataSource<IEnumerable<ParityTxTraceFromStore>> receipt1Source = GetSource<IEnumerable<ParityTxTraceFromStore>>(uri1, TraceModuleFactory.Converters);
            using IConsensusDataSource<IEnumerable<ParityTxTraceFromStore>> receipt2Source = GetSource<IEnumerable<ParityTxTraceFromStore>>(uri2, TraceModuleFactory.Converters);
            TrySetData(blockNumber, receipt1Source, receipt2Source);
            await CompareCollection(receipt1Source, receipt2Source);
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

        private IConsensusDataSource<T> GetSource<T>(Uri uri, IEnumerable<JsonConverter> additionalConverters = null) 
        {
            var serializer = GetSerializer(additionalConverters);
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

        private IJsonSerializer GetSerializer(IEnumerable<JsonConverter> additionalConverters)
        {
            IJsonSerializer jsonSerializer = new EthereumJsonSerializer();
            if (additionalConverters != null)
            {
                jsonSerializer.RegisterConverters(additionalConverters);
            }

            return jsonSerializer;
        }

        private static async Task Compare<T>(IConsensusDataSource<T> source1,
            IConsensusDataSource<T> source2, 
            Func<EquivalencyAssertionOptions<T>, EquivalencyAssertionOptions<T>> options = null)
        {
            T data = await source1.GetData();
            T expectation = await source2.GetData();
            data.Should().BeEquivalentTo(expectation, options ?? (o => o));
        }
        
        private static async Task CompareCollection<T>(IConsensusDataSource<IEnumerable<T>> source1,
            IConsensusDataSource<IEnumerable<T>> source2, 
            Func<EquivalencyAssertionOptions<T>, EquivalencyAssertionOptions<T>> options = null)
        {
            IEnumerable<T> data = await source1.GetData();
            IEnumerable<T> expectation = await source2.GetData();
            data.Should().BeEquivalentTo(expectation, options ?? (o => o));
        }

        private interface IConsensusDataSource<T> : IDisposable
        {
            Task<T> GetData();
        }

        private interface IConsensusDataSourceWithParameter<TData>
        {
            TData Parameter { get; set; }
        }
    }
}
