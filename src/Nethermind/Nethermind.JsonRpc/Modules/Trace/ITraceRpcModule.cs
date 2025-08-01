// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.JsonRpc.Data;

namespace Nethermind.JsonRpc.Modules.Trace
{
    [RpcModule(ModuleType.Trace)]
    public interface ITraceRpcModule : IRpcModule
    {
        [JsonRpcMethod(Description = "", IsImplemented = true, IsSharable = false)]
        ResultWrapper<ParityTxTraceFromReplay> trace_call(TransactionForRpc call,
            [JsonRpcParameter(Description = "Possible values : [\"VmTrace\", \"StateDiff\", \"Trace\", \"Rewards\", \"All\"]")] string[] traceTypes,
            BlockParameter? blockParameter = null, Dictionary<Address, AccountOverride>? stateOverride = null);

        [JsonRpcMethod(Description = "Performs multiple traces on top of a block",
            IsImplemented = true,
            IsSharable = false,
            ExampleResponse = """{"jsonrpc":"2.0","result":[{"output":"0x","stateDiff":null,"trace":[{"action":{"callType":"call","from":"0x78c0d804cc8bd42c758619a054c69ff3db91b625","gas":"0x5f58ef8","input":"0x","to":"0xa94f5374fce5edbc8e2a8697c15331677e6ebf0b","value":"0x1234"},"result":{"gasUsed":"0x0","output":"0x"},"subtraces":0,"traceAddress":[],"type":"call"}],"vmTrace":null},{"output":"0x","stateDiff":null,"trace":[{"action":{"callType":"call","from":"0x78c0d804cc8bd42c758619a054c69ff3db91b625","gas":"0x5f58ef8","input":"0x","to":"0xa94f5374fce5edbc8e2a8697c15331677e6ebf0b","value":"0x5678"},"result":{"gasUsed":"0x0","output":"0x"},"subtraces":0,"traceAddress":[],"type":"call"}],"vmTrace":null}],"id":1}""")]
        ResultWrapper<IEnumerable<ParityTxTraceFromReplay>> trace_callMany(
            [JsonRpcParameter(ExampleValue = """[[{"from":"0x78c0d804cc8bd42c758619a054c69ff3db91b625","to":"0xa94f5374fce5edbc8e2a8697c15331677e6ebf0b","value":"0x1234"},["trace"]],[{"from":"0x78c0d804cc8bd42c758619a054c69ff3db91b625","to":"0xa94f5374fce5edbc8e2a8697c15331677e6ebf0b","value":"0x5678"},["trace"]]]""")] TransactionForRpcWithTraceTypes[] calls,
            [JsonRpcParameter(ExampleValue = "\"latest\"")] BlockParameter? blockParameter = null);

        [JsonRpcMethod(Description = "Traces a call to eth_sendRawTransaction without making the call, returning the traces",
            IsImplemented = true,
            IsSharable = false,
            ExampleResponse = "\"output\":\"0x\",\"stateDiff\":null,\"trace\":[{\"action\":{\"callType\":\"call\",\"from\":\"0xc451c26cc24c25e46b148ac4716804c12c34e7d2\",\"gas\":\"0x0\",\"input\":\"0x\",\"to\":\"0xb943b13292086848d8180d75c73361107920bb1a\",\"value\":\"0x0\"},\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"type\":\"call\"}],\"vmTrace\":null")]
        ResultWrapper<ParityTxTraceFromReplay> trace_rawTransaction([JsonRpcParameter(ExampleValue = "[\"0xf86380843b9aca0082520894b943b13292086848d8180d75c73361107920bb1a80802ea0385656b91b8f1f5139e9ba3449b946a446c9cfe7adb91b180ddc22c33b17ac4da01fe821879d386b140fd8080dcaaa98b8c709c5025c8c4dea1334609ebac41b6c\",[\"trace\"]]")] byte[] data, [JsonRpcParameter(Description = "Possible values : [\"VmTrace\", \"StateDiff\", \"Trace\", \"Rewards\", \"All\"]")] string[] traceTypes);

        [JsonRpcMethod(Description = "",
            IsImplemented = true,
            IsSharable = false,
            ExampleResponse = "{\"output\":\"0x\",\"stateDiff\":null,\"trace\":[{\"action\":{\"callType\":\"call\",\"from\":\"0x3c436c8ec40e0679fe64168545812ac13220f150\",\"gas\":\"0xc118\",\"input\":\"0xd46eb119\",\"to\":\"0x9e00de186f33e9fac9e28d69127f7f637b96c177\",\"value\":\"0xde0b6b3a7640000\"},\"result\":{\"gasUsed\":\"0xc118\",\"output\":\"0x\"},\"subtraces\":4,\"traceAddress\":[],\"type\":\"call\"},{\"action\":{\"callType\":\"call\",\"from\":\"0x9e00de186f33e9fac9e28d69127f7f637b96c177\",\"gas\":\"0xa965\",\"input\":\"0x40c10f190000000000000000000000009e00de186f33e9fac9e28d69127f7f637b96c1770000000000000000000000000000000000000000000000000de0b6b3a7640000\",\"to\":\"0x766cd52cb91f4d2d7ea8b4c175aff0aba3696be1\",\"value\":\"0x0\"},\"result\":{\"gasUsed\":\"0x76b8\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[0],\"type\":\"call\"}, (...)}]}")]
        ResultWrapper<ParityTxTraceFromReplay> trace_replayTransaction([JsonRpcParameter(ExampleValue = "[\"0x203abf19610ce15bc509d4b341e907ff8c5a8287ae61186fd4da82146408c28c\",[\"trace\"]]")] Hash256 txHash, [JsonRpcParameter(Description = "Possible values : [\"VmTrace\", \"StateDiff\", \"Trace\", \"Rewards\", \"All\"]")] string[] traceTypes);

        [JsonRpcMethod(Description = "", IsImplemented = true, IsSharable = false, ExampleResponse = "[{\"output\":\"0x0000000000000000000000000000000000000000000000000000000000000001\",\"stateDiff\":null,\"trace\":[{\"action\":{\"callType\":\"call\",\"from\":\"0x37f207b3ebda37de11ad2b6d306464e313c4841a\",\"gas\":\"0x3c36\",\"input\":\"0xa9059cbb000000000000000000000000d20d2f4c0b595abedef821a4157b0b990a37dae60000000000000000000000000000000000000000000000008ac7230489e80000\",\"to\":\"0x59a524d1f5dcbde3224fd42171795283596a8103\",\"value\":\"0x0\"},\"result\":{\"gasUsed\":\"0x3c36\",\"output\":\"0x0000000000000000000000000000000000000000000000000000000000000001\"},\"subtraces\":0,\"traceAddress\":[],\"type\":\"call\"}],\"transactionHash\":\"0x17dc0fef36bb997c79ee2a0a126d059227000a2d47c9bbd1f49b5902a4e7385a\",\"vmTrace\":null}, (...)]")]
        ResultWrapper<IEnumerable<ParityTxTraceFromReplay>> trace_replayBlockTransactions([JsonRpcParameter(ExampleValue = "[\"0x88df42\",[\"trace\"]]")] BlockParameter blockParameter, [JsonRpcParameter(Description = "Possible values : [\"VmTrace\", \"StateDiff\", \"Trace\", \"Rewards\", \"All\"]")] string[] traceTypes);

        [JsonRpcMethod(Description = "", IsImplemented = true, IsSharable = false)]
        ResultWrapper<IEnumerable<ParityTxTraceFromStore>> trace_filter(TraceFilterForRpc traceFilterForRpc);

        [JsonRpcMethod(Description = "",
            IsImplemented = true,
            IsSharable = false,
            ExampleResponse = "{\"action\":{\"callType\":\"call\",\"from\":\"0x31b98d14007bdee637298086988a0bbd31184523\",\"gas\":\"0x0\",\"input\":\"0x\",\"to\":\"0x0e8cda5d7ebda67606a9b296a9dd4351bca1d263\",\"value\":\"0x1043561a882930000\"},\"blockHash\":\"0x6537c92f1fae55d9ea9b0fb25744262114b09e50ac320d7d839830f8c4d723a0\",\"blockNumber\":8969312,\"result\":{\"gasUsed\":\"0x0\",\"output\":\"0x\"},\"subtraces\":0,\"traceAddress\":[],\"transactionHash\":\"0xf4860fc1dc22404b85db7d666dfae65dec7cdcb196837a67ffa992d709f78b9e\",\"transactionPosition\":11,\"type\":\"call\"},{\"action\":{\"callType\":\"call\",\"from\":\"0x71c95151c960aa3976b462ff41adb328790f110d\",\"gas\":\"0x7205\",\"input\":\"0x095ea7b3000000000000000000000000c5992c0e0a3267c7f75493d0f717201e26be35f7ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff\",\"to\":\"0x5592ec0cfb4dbc12d3ab100b257153436a1f0fea\",\"value\":\"0x0\"},\"blockHash\":\"0x6537c92f1fae55d9ea9b0fb25744262114b09e50ac320d7d839830f8c4d723a0\",\"blockNumber\":8969312,\"result\":{\"gasUsed\":\"0x5fdd\",\"output\":\"0x0000000000000000000000000000000000000000000000000000000000000001\"},\"subtraces\":0,\"traceAddress\":[],\"transactionHash\":\"0xec216ca7e754ea289dd59fc7f9f2c9a5b90668afb5a52d49ee15c3c5fd559b3b\",\"transactionPosition\":12,\"type\":\"call\"}")]
        ResultWrapper<IEnumerable<ParityTxTraceFromStore>> trace_block([JsonRpcParameter(ExampleValue = "\"latest\"")] BlockParameter numberOrTag, string? forkName = null);

        [JsonRpcMethod(Description = "Returns trace at given position.",
            IsImplemented = true,
            IsSharable = false,
            ExampleResponse = """{"jsonrpc":"2.0","result":[{"action":{"callType":"call","from":"0x1c39ba39e4735cb65978d4db400ddd70a72dc750","gas":"0x13e99","input":"0x16c72721","to":"0x2bd2326c993dfaef84f696526064ff22eba5b362","value":"0x0"},"blockHash":"0x7eb25504e4c202cf3d62fd585d3e238f592c780cca82dacb2ed3cb5b38883add","blockNumber":3068185,"result":{"gasUsed":"0x183","output":"0x0000000000000000000000000000000000000000000000000000000000000001"},"subtraces":0,"traceAddress":[0],"transactionHash":"0x17104ac9d3312d8c136b7f44d4b8b47852618065ebfa534bd2d3b5ef218ca1f3","transactionPosition":2,"type":"call"}],"id":1}""")]
        ResultWrapper<IEnumerable<ParityTxTraceFromStore>> trace_get(
            [JsonRpcParameter(ExampleValue = "0x17104ac9d3312d8c136b7f44d4b8b47852618065ebfa534bd2d3b5ef218ca1f3")] Hash256 txHash,
            [JsonRpcParameter(ExampleValue = """["0x0","0x1"]""")] long[] positions);

        [JsonRpcMethod(Description = "", IsImplemented = true,
            IsSharable = false,
            ExampleResponse = "[{\"action\":{\"callType\":\"call\",\"from\":\"0x3c436c8ec40e0679fe64168545812ac13220f150\",\"gas\":\"0xc118\",\"input\":\"0xd46eb119\",\"to\":\"0x9e00de186f33e9fac9e28d69127f7f637b96c177\",\"value\":\"0xde0b6b3a7640000\"},\"blockHash\":\"0xf40b4c9faaeaf116a50380ce3795297bc02068b062f1797cd507875347c3372e\",\"blockNumber\":8970132,\"result\":{\"gasUsed\":\"0xc118\",\"output\":\"0x\"},\"subtraces\":4,\"traceAddress\":[],\"transactionHash\":\"0x203abf19610ce15bc509d4b341e907ff8c5a8287ae61186fd4da82146408c28c\",\"transactionPosition\":9,\"type\":\"call\"},(...)]")]
        ResultWrapper<IEnumerable<ParityTxTraceFromStore>> trace_transaction([JsonRpcParameter(ExampleValue = "\"0x203abf19610ce15bc509d4b341e907ff8c5a8287ae61186fd4da82146408c28c\"")] Hash256 txHash);

        [JsonRpcMethod(Description = "Returns parity like traces for simulated blocks", IsImplemented = true, IsSharable = false)]
        ResultWrapper<IReadOnlyList<SimulateBlockResult<ParityLikeTxTrace>>> trace_simulateV1(SimulatePayload<TransactionForRpc> payload, BlockParameter? blockParameter = null, string[]? traceTypes = null);
    }
}
