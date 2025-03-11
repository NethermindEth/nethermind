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
        Func<TestRpcBlockchain, Transaction[]> transactions = b =>
        {
            var nonce = b.ReadOnlyState.GetNonce(TestItem.AddressA);
            var contract = Prepare.EvmCode
                .PushData(0)
                .PushData(32)
                .Op(Instruction.SSTORE)
                .Op(Instruction.STOP)
                .Done;

            var salt = new byte[32];
            var deployment = Prepare.EvmCode
                .Create2(contract, salt, 0)
                .Op(Instruction.STOP)
                .Done;

            Address deployingContractAddress = ContractAddress.From(TestItem.PrivateKeyA.Address, nonce);
            Address deploymentAddress = ContractAddress.From(deployingContractAddress, salt, contract);

            var call = Prepare.EvmCode
                .Call(deploymentAddress, 100000)
                .Op(Instruction.STOP)
                .Done;

            return
            [
                Build.A.Transaction
                    .WithNonce(nonce)
                    .WithCode(deployment)
                    .WithGasLimit(100000)
                    .SignedAndResolved(TestItem.PrivateKeyA)
                    .TestObject,

                Build.A.Transaction
                    .WithNonce(nonce + 1)
                    .WithCode(call)
                    .WithGasLimit(100000)
                    .SignedAndResolved(TestItem.PrivateKeyA)
                    .TestObject,
            ];
        };

        yield return new TestCaseData(
            transactions,
            new GethTraceOptions(),
            """
            {
                "jsonrpc": "2.0",
                "result": [
                    {
                        "result": {
                            "gas": 87700,
                            "failed": false,
                            "returnValue": "0x",
                            "structLogs": [
                                { "pc": 0, "op": "PUSH32", "gas": 46536,  "gasCost": 3, "depth": 1,  "error": null,  "stack": [], "storage": {} },
                                { "pc": 33, "op": "PUSH1", "gas": 46533,  "gasCost": 3, "depth": 1,  "error": null, "stack": ["0x6000602055000000000000000000000000000000000000000000000000000000"], "storage": {} },
                                { "pc": 35, "op": "MSTORE", "gas": 46530,  "gasCost": 6, "depth": 1,  "error": null, "stack": ["0x6000602055000000000000000000000000000000000000000000000000000000", "0x0"], "storage": {} },
                                { "pc": 36, "op": "PUSH32", "gas": 46524,  "gasCost": 3, "depth": 1,  "error": null, "stack": [], "storage": {} },
                                { "pc": 69, "op": "PUSH1", "gas": 46521,  "gasCost": 3, "depth": 1,  "error": null, "stack": ["0x0"], "storage": {} },
                                { "pc": 71, "op": "PUSH1", "gas": 46518,  "gasCost": 3, "depth": 1,  "error": null,  "stack": ["0x0", "0x6"], "storage": {} },
                                { "pc": 73, "op": "PUSH1", "gas": 46515,  "gasCost": 3, "depth": 1,  "error": null,  "stack": ["0x0", "0x6", "0x0"], "storage": {} },
                                { "pc": 75, "op": "CREATE2", "gas": 46512,  "gasCost": 32006, "depth": 1,  "error": null,  "stack": ["0x0", "0x6", "0x0", "0x0"], "storage": {} },
                                { "pc": 0, "op": "PUSH1", "gas": 14280,  "gasCost": 3, "depth": 2,  "error": null,  "stack": [], "storage": {} },
                                { "pc": 2, "op": "PUSH1", "gas": 14277,  "gasCost": 3, "depth": 2,  "error": null,  "stack": ["0x0"], "storage": {} },
                                { "pc": 4, "op": "SSTORE", "gas": 14274,  "gasCost": 2200, "depth": 2,  "error": null,  "stack": ["0x0", "0x20"], "storage": {} },
                                { "pc": 5, "op": "STOP", "gas": 12074,  "gasCost": 0, "depth": 2,  "error": null,  "stack": [], "storage": {} },
                                { "pc": 76, "op": "STOP", "gas": 12300,  "gasCost": 0, "depth": 1,  "error": null,  "stack": ["0x28156f6fdeeffd5667d51bb8d7d5069a920e0837"], "storage": {} }
                            ]
                        },
                        "txHash": "0xb5a78a1eda0ae98d4f62eec3e0b7f5bf81810cd57bc75006b611982667bcdbe7"
                    },
                    {
                        "result": {
                            "gas": 56141,
                            "failed": false,
                            "returnValue": "0x",
                            "structLogs": [
                                { "pc": 0, "op": "PUSH1", "gas": 46480,  "gasCost": 3, "depth": 1,  "error": null,  "stack": [], "storage": {} },
                                { "pc": 2, "op": "PUSH1", "gas": 46477,  "gasCost": 3, "depth": 1,  "error": null,  "stack": ["0x0"], "storage": {} },
                                { "pc": 4, "op": "PUSH1", "gas": 46474,  "gasCost": 3, "depth": 1,  "error": null,  "stack": ["0x0", "0x0"], "storage": {} },
                                { "pc": 6, "op": "PUSH1", "gas": 46471,  "gasCost": 3, "depth": 1,  "error": null,  "stack": ["0x0", "0x0", "0x0"], "storage": {} },
                                { "pc": 8, "op": "PUSH1", "gas": 46468,  "gasCost": 3, "depth": 1,  "error": null,  "stack": ["0x0", "0x0", "0x0", "0x0"], "storage": {} },
                                { "pc": 10, "op": "PUSH20", "gas": 46465,  "gasCost": 3, "depth": 1,  "error": null,  "stack": ["0x0", "0x0", "0x0", "0x0", "0x0"], "storage": {} },
                                { "pc": 31, "op": "PUSH3", "gas": 46462,  "gasCost": 3, "depth": 1,  "error": null,  "stack": ["0x0", "0x0", "0x0", "0x0", "0x0", "0x28156f6fdeeffd5667d51bb8d7d5069a920e0837"], "storage": {} },
                                { "pc": 35, "op": "CALL", "gas": 46459,  "gasCost": 45774, "depth": 1,  "error": null,  "stack": ["0x0", "0x0", "0x0", "0x0", "0x0", "0x28156f6fdeeffd5667d51bb8d7d5069a920e0837", "0x186a0"], "storage": {} },
                                { "pc": 36, "op": "STOP", "gas": 43859,  "gasCost": 0, "depth": 1,  "error": null,  "stack": ["0x1"], "storage": {} }
                            ]
                        },
                        "txHash": "0xdb3d8694a97364e8628aeb18993520ea6bac0b65b02eed1abddaaed1ddd04e7b"
                    }
                ],
                "id": 67
            }
            """
        )
        { TestName = "Contract with blockMemoryTracer" };

        yield return new TestCaseData(
            transactions,
            new GethTraceOptions { Tracer = "{gasUsed: [], step: function(log) { this.gasUsed.push(log.getGas()); }, result: function() { return this.gasUsed; }, fault: function(){}}" },
            """
            {
                "jsonrpc": "2.0",
                "result": [
                    {
                        "result": [46536,46533,46530,46524,46521,46518,46515,46512,14280,14277,14274,12074,12300],
                        "txHash": "0xb5a78a1eda0ae98d4f62eec3e0b7f5bf81810cd57bc75006b611982667bcdbe7"
                    },
                    {
                        "result": [46480,46477,46474,46471,46468,46465,46462,46459,43859],
                        "txHash": "0xdb3d8694a97364e8628aeb18993520ea6bac0b65b02eed1abddaaed1ddd04e7b"
                    }
                ],
                "id": 67
            }
            """
        )
        { TestName = "Contract with javaScriptTracer" };

        yield return new TestCaseData(
            transactions,
            new GethTraceOptions { Tracer = Native4ByteTracer.FourByteTracer },
            """
            {
                "jsonrpc": "2.0",
                "result": [
                    {
                        "result": {
                            "0x7f600060-73": 1
                        },
                        "txHash": "0xb5a78a1eda0ae98d4f62eec3e0b7f5bf81810cd57bc75006b611982667bcdbe7"
                    },
                    {
                        "result": {
                            "0x60006000-33": 1
                        },
                        "txHash": "0xdb3d8694a97364e8628aeb18993520ea6bac0b65b02eed1abddaaed1ddd04e7b"
                    }
                ],
                "id": 67
            }
            """
        )
        { TestName = "Contract with " + Native4ByteTracer.FourByteTracer };

        yield return new TestCaseData(
            transactions,
            new GethTraceOptions { Tracer = NativeCallTracer.CallTracer },
            """
            {
                "jsonrpc": "2.0",
                "result": [
                    {
                        "result": {
                            "type": "CREATE",
                            "from": "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099",
                            "to": "0x0ffd3e46594919c04bcfd4e146203c8255670828",
                            "value": "0x1",
                            "gas": "0x186a0",
                            "gasUsed": "0x15694",
                            "input": "0x7f60006020550000000000000000000000000000000000000000000000000000006000527f0000000000000000000000000000000000000000000000000000000000000000600660006000f500",
                            "calls": [
                                {
                                    "type": "CREATE2",
                                    "from": "0x0ffd3e46594919c04bcfd4e146203c8255670828",
                                    "to": "0x28156f6fdeeffd5667d51bb8d7d5069a920e0837",
                                    "value": "0x0",
                                    "gas": "0x37c8",
                                    "gasUsed": "0x89e",
                                    "input": "0x600060205500"
                                }
                            ]
                        },
                        "txHash": "0xb5a78a1eda0ae98d4f62eec3e0b7f5bf81810cd57bc75006b611982667bcdbe7"
                    },
                    {
                        "result": {
                            "type": "CREATE",
                            "from": "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099",
                            "to": "0x6b5887043de753ecfa6269f947129068263ffbe2",
                            "value": "0x1",
                            "gas": "0x186a0",
                            "gasUsed": "0xdb4d",
                            "input": "0x600060006000600060007328156f6fdeeffd5667d51bb8d7d5069a920e0837620186a0f100",
                            "calls": [
                                {
                                    "type": "CALL",
                                    "from": "0x6b5887043de753ecfa6269f947129068263ffbe2",
                                    "to": "0x28156f6fdeeffd5667d51bb8d7d5069a920e0837",
                                    "value": "0x0",
                                    "gas": "0xa8a6",
                                    "gasUsed": "0x0",
                                    "input": "0x"
                                }
                            ]
                        },
                        "txHash": "0xdb3d8694a97364e8628aeb18993520ea6bac0b65b02eed1abddaaed1ddd04e7b"
                    }
                ],
                "id": 67
            }
            """
        )
        { TestName = "Contract with " + NativeCallTracer.CallTracer };

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
                            "0x0ffd3e46594919c04bcfd4e146203c8255670828": {
                                "balance": "0x0"
                            },
                            "0x475674cb523a0a2736b7f7534390288fce16982c": {
                                "balance": "0xf618"
                            },
                            "0x28156f6fdeeffd5667d51bb8d7d5069a920e0837": {
                                "balance": "0x0",
                                "storage": {
                                    "0x0000000000000000000000000000000000000000000000000000000000000020": "0x0000000000000000000000000000000000000000000000000000000000000000"
                                }
                            }
                        },
                        "txHash": "0xb5a78a1eda0ae98d4f62eec3e0b7f5bf81810cd57bc75006b611982667bcdbe7"
                    },
                    {
                        "result": {
                            "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099": {
                                "balance": "0x3635c9adc5de9db350",
                                "nonce": 4,
                                "code": "0xabcd"
                            },
                            "0x6b5887043de753ecfa6269f947129068263ffbe2": {
                                "balance": "0x0"
                            },
                            "0x475674cb523a0a2736b7f7534390288fce16982c": {
                                "balance": "0x24cac"
                            },
                            "0x28156f6fdeeffd5667d51bb8d7d5069a920e0837": {
                                "balance": "0x0",
                                "nonce": 1
                            }
                        },
                        "txHash": "0xdb3d8694a97364e8628aeb18993520ea6bac0b65b02eed1abddaaed1ddd04e7b"
                    }
                ],
                "id": 67
            }
            """
        )
        { TestName = "Contract with " + NativePrestateTracer.PrestateTracer };
    }

}
