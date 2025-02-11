// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Evm.Tracing.GethStyle.Custom.Native.Call;
using Nethermind.Evm.Tracing.GethStyle.Custom.Native.FourByte;
using Nethermind.Evm.Tracing.GethStyle.Custom.Native.Prestate;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

public partial class DebugRpcModuleTests
{
    [Test]
    public async Task Debug_traceBlock_with_invalid_rlp()
    {
        using Context context = await Context.Create();

        const string expected = """{"jsonrpc":"2.0","error":{"code":-32602,"message":"Invalid params"},"id":67}""";
        var response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceBlock", "xxx");

        Assert.That(JsonElement.DeepEquals(
            JsonDocument.Parse(response).RootElement,
            JsonDocument.Parse(expected).RootElement),
            response);
    }

    [TestCaseSource(nameof(TraceBlockSource))]
    public async Task Debug_traceBlock(Func<TestRpcBlockchain, Transaction[]> factory, GethTraceOptions options, string expected)
    {
        using Context context = await Context.Create();

        await context.Blockchain.AddBlock(factory(context.Blockchain));

        var rlp = Rlp.Encode(context.Blockchain.BlockTree.Head).ToString();
        var response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceBlock", rlp, options);

        Assert.That(JsonElement.DeepEquals(
                JsonDocument.Parse(response).RootElement,
                JsonDocument.Parse(expected).RootElement),
            response);
    }

    [TestCaseSource(nameof(TraceBlockSource))]
    public async Task Debug_traceBlockByNumber(Func<TestRpcBlockchain, Transaction[]> factory, GethTraceOptions options, string expected)
    {
        using Context context = await Context.Create();

        await context.Blockchain.AddBlock(factory(context.Blockchain));

        var blockNumber = context.Blockchain.BlockTree.Head!.Number;
        var response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceBlockByNumber", blockNumber, options);

        Assert.That(JsonElement.DeepEquals(
                JsonDocument.Parse(response).RootElement,
                JsonDocument.Parse(expected).RootElement),
            response);
    }

    [TestCaseSource(nameof(TraceBlockSource))]
    public async Task Debug_traceBlockByHash(Func<TestRpcBlockchain, Transaction[]> factory, GethTraceOptions options, string expected)
    {
        using Context context = await Context.Create();

        await context.Blockchain.AddBlock(factory(context.Blockchain));

        var blockHash = context.Blockchain.BlockTree.Head!.Hash;
        var response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceBlockByHash", blockHash, options);

        Assert.That(JsonElement.DeepEquals(
                JsonDocument.Parse(response).RootElement,
                JsonDocument.Parse(expected).RootElement),
            response);
    }

    private static IEnumerable<TestCaseData> TraceBlockSource()
    {
        var contract = Prepare.EvmCode
            .PushData(0)
            .PushData(32)
            .Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Done;

        var deployment = Prepare.EvmCode
            .Create(contract, 0)
            .Op(Instruction.STOP)
            .Done;

        var call = Prepare.EvmCode
            .Call(TestItem.AddressB, 100000)
            .Op(Instruction.STOP)
            .Done;

        Func<TestRpcBlockchain, Transaction[]> transactions = b =>
        [
            Build.A.Transaction
                .WithNonce(b.State.GetNonce(TestItem.AddressA))
                .WithCode(deployment)
                .To(TestItem.AddressB)
                .WithGasLimit(100000)
                .SignedAndResolved(TestItem.PrivateKeyA)
                .TestObject,

            Build.A.Transaction
                .WithNonce(b.State.GetNonce(TestItem.AddressA) + 1)
                .WithCode(call)
                .To(TestItem.AddressB)
                .WithGasLimit(100000)
                .SignedAndResolved(TestItem.PrivateKeyA)
                .TestObject,
        ];

        yield return new TestCaseData(
            transactions,
            new GethTraceOptions(),
            """
            {
                "jsonrpc": "2.0",
                "result": [
                    {
                        "result": {
                            "gas": 21320,
                            "failed": false,
                            "returnValue": "",
                            "structLogs": []
                        },
                        "txHash": "0x4a920824e30174e7868d25bd84903e5b6c8f323f02bd3b04a2b1bff26aa1697c"
                    },
                    {
                        "result": {
                            "gas": 21520,
                            "failed": false,
                            "returnValue": "",
                            "structLogs": []
                        },
                        "txHash": "0xb1916d32b005a321b101996cd598f85562cfc6c53d035507d808ca38b6f04b36"
                    }
                ],
                "id": 67
            }
            """
        ) { TestName = "Contract with blockMemoryTracer" };

        yield return new TestCaseData(
            transactions,
            new GethTraceOptions { Tracer = "{gasUsed: [], step: function(log) { this.gasUsed.push(log.getGas()); }, result: function() { return this.gasUsed; }, fault: function(){}}" },
            """
            {
                "jsonrpc": "2.0",
                "result": [
                    {
                        "result": [],
                        "txHash": "0x4a920824e30174e7868d25bd84903e5b6c8f323f02bd3b04a2b1bff26aa1697c"
                    },
                    {
                        "result": [],
                        "txHash": "0xb1916d32b005a321b101996cd598f85562cfc6c53d035507d808ca38b6f04b36"
                    }
                ],
                "id": 67
            }
            """
        ) { TestName = "Contract with javaScriptTracer" };

        yield return new TestCaseData(
            transactions,
            new GethTraceOptions { Tracer = Native4ByteTracer.FourByteTracer },
            """
            {
                "jsonrpc": "2.0",
                "result": [
                    {
                        "result": {
                            "0x7f600060-40": 1
                        },
                        "txHash": "0x4a920824e30174e7868d25bd84903e5b6c8f323f02bd3b04a2b1bff26aa1697c"
                    },
                    {
                        "result": {
                            "0x60006000-33": 1
                        },
                        "txHash": "0xb1916d32b005a321b101996cd598f85562cfc6c53d035507d808ca38b6f04b36"
                    }
                ],
                "id": 67
            }
            """
        ) { TestName = "Contract with " + Native4ByteTracer.FourByteTracer };

        yield return new TestCaseData(
            transactions,
            new GethTraceOptions { Tracer = NativeCallTracer.CallTracer },
            """
            {
                "jsonrpc": "2.0",
                "result": [
                    {
                        "result": {
                            "type": "CALL",
                            "from": "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099",
                            "to": "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
                            "value": "0x1",
                            "gas": "0x186a0",
                            "gasUsed": "0x5348",
                            "input": "0x7f6000602055000000000000000000000000000000000000000000000000000000600052600660006000f000"
                        },
                        "txHash": "0x4a920824e30174e7868d25bd84903e5b6c8f323f02bd3b04a2b1bff26aa1697c"
                    },
                    {
                        "result": {
                            "type": "CALL",
                            "from": "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099",
                            "to": "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
                            "value": "0x1",
                            "gas": "0x186a0",
                            "gasUsed": "0x5410",
                            "input": "0x6000600060006000600073942921b14f1b1c385cd7e0cc2ef7abe5598c8358620186a0f100"
                        },
                        "txHash": "0xb1916d32b005a321b101996cd598f85562cfc6c53d035507d808ca38b6f04b36"
                    }
                ],
                "id": 67
            }
            """
        ) { TestName = "Contract with " + NativeCallTracer.CallTracer };

        yield return new TestCaseData(
            transactions,
            new GethTraceOptions { Tracer = NativePrestateTracer.PrestateTracer },
            """
            {
                "jsonrpc": "2.0",
                "result": [
                    {
                        "result": {
                            "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099": {
                                "balance": "0x3635c9adc5de9f09e5",
                                "nonce": 3,
                                "code": "0xabcd"
                            },
                            "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358": {
                                "balance": "0x3635c9adc5dea00003"
                            },
                            "0x475674cb523a0a2736b7f7534390288fce16982c": {
                                "balance": "0xf618"
                            }
                        },
                        "txHash": "0x4a920824e30174e7868d25bd84903e5b6c8f323f02bd3b04a2b1bff26aa1697c"
                    },
                    {
                        "result": {
                            "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099": {
                                "balance": "0x3635c9adc5de9eb69c",
                                "nonce": 4,
                                "code": "0xabcd"
                            },
                            "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358": {
                                "balance": "0x3635c9adc5dea00004"
                            },
                            "0x475674cb523a0a2736b7f7534390288fce16982c": {
                                "balance": "0x14960"
                            }
                        },
                        "txHash": "0xb1916d32b005a321b101996cd598f85562cfc6c53d035507d808ca38b6f04b36"
                    }
                ],
                "id": 67
            }
            """
        ) { TestName = "Contract with " + NativePrestateTracer.PrestateTracer };
    }

}
