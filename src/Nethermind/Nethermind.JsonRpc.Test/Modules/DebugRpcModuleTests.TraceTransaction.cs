// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
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
    [TestCaseSource(nameof(TraceTransactionTransferSource))]
    [TestCaseSource(nameof(TraceTransactionContractSource))]
    public async Task Debug_traceTransaction(Func<TestRpcBlockchain, Transaction> factory, GethTraceOptions options, string expected)
    {
        using Context context = await Context.Create();

        var transaction = factory(context.Blockchain);
        await context.Blockchain.AddBlock(transaction);

        var response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceTransaction", transaction.Hash, options);

        Assert.That(JsonElement.DeepEquals(
            JsonDocument.Parse(response).RootElement,
            JsonDocument.Parse(expected).RootElement),
            response);
    }

    [TestCaseSource(nameof(TraceTransactionTransferSource))]
    [TestCaseSource(nameof(TraceTransactionContractSource))]
    public async Task Debug_traceTransactionByBlockAndIndex(Func<TestRpcBlockchain, Transaction> factory, GethTraceOptions options, string expected)
    {
        using Context context = await Context.Create();

        var transaction = factory(context.Blockchain);
        await context.Blockchain.AddBlock(transaction);

        var blockNumber = context.Blockchain.BlockTree.Head!.Number;
        var response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceTransactionByBlockAndIndex", blockNumber, 0, options);

        Assert.That(JsonElement.DeepEquals(
            JsonDocument.Parse(response).RootElement,
            JsonDocument.Parse(expected).RootElement),
            response);
    }

    [TestCaseSource(nameof(TraceTransactionTransferSource))]
    [TestCaseSource(nameof(TraceTransactionContractSource))]
    public async Task Debug_traceTransactionByBlockhashAndIndex(Func<TestRpcBlockchain, Transaction> factory, GethTraceOptions options, string expected)
    {
        using Context context = await Context.Create();

        var transaction = factory(context.Blockchain);
        await context.Blockchain.AddBlock(transaction);

        var blockHash = context.Blockchain.BlockTree.Head!.Hash;
        var response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceTransactionByBlockhashAndIndex", blockHash, 0, options);

        Assert.That(JsonElement.DeepEquals(
            JsonDocument.Parse(response).RootElement,
            JsonDocument.Parse(expected).RootElement),
            response);
    }

    [TestCaseSource(nameof(TraceTransactionTransferSource))]
    [TestCaseSource(nameof(TraceTransactionContractSource))]
    public async Task Debug_traceTransactionInBlockByHash(Func<TestRpcBlockchain, Transaction> factory, GethTraceOptions options, string expected)
    {
        using Context context = await Context.Create();

        var transaction = factory(context.Blockchain);
        await context.Blockchain.AddBlock(transaction);

        var block = context.Blockchain.BlockTree.Head!;
        var blockRlp = Rlp.Encode(block).ToString();
        
        // Use the transaction hash as it exists in the block, not the original transaction hash
        var blockTransaction = block.Transactions[^1]; // Get the last transaction (the one we just added)
        var response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceTransactionInBlockByHash", blockRlp, blockTransaction.Hash, options);

        Assert.That(JsonElement.DeepEquals(
            JsonDocument.Parse(response).RootElement,
            JsonDocument.Parse(expected).RootElement),
            response);
    }

    [TestCaseSource(nameof(TraceTransactionTransferSource))]
    [TestCaseSource(nameof(TraceTransactionContractSource))]
    public async Task Debug_traceTransactionInBlockByIndex(Func<TestRpcBlockchain, Transaction> factory, GethTraceOptions options, string expected)
    {
        using Context context = await Context.Create();

        var transaction = factory(context.Blockchain);
        await context.Blockchain.AddBlock(transaction);

        var block = context.Blockchain.BlockTree.Head!;
        var blockRlp = Rlp.Encode(block).ToString();
        
        // Use the correct transaction index - the transaction we added should be the last one in the block
        var txIndex = block.Transactions.Length - 1;
        var response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceTransactionInBlockByIndex", blockRlp, txIndex, options);

        Assert.That(JsonElement.DeepEquals(
            JsonDocument.Parse(response).RootElement,
            JsonDocument.Parse(expected).RootElement),
            response);
    }

    private static IEnumerable<TestCaseData> TraceTransactionTransferSource()
    {
        var transferTransaction = (TestRpcBlockchain b) => Build.A.Transaction
            .WithNonce(b.ReadOnlyState.GetNonce(TestItem.AddressA))
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        yield return new TestCaseData(
            transferTransaction,
            new GethTraceOptions(),
            """{"jsonrpc":"2.0","result":{"gas":21000,"failed":false,"returnValue":"0x","structLogs":[]},"id":67}"""
        )
        { TestName = "Transfer with blockMemoryTracer" };

        yield return new TestCaseData(
            transferTransaction,
            new GethTraceOptions { Tracer = "{gasUsed: [], step: function(log) { this.gasUsed.push(log.getGas()); }, result: function() { return this.gasUsed; }, fault: function(){}}" },
            """{"jsonrpc":"2.0","result":[],"id":67}"""
        )
        { TestName = "Transfer with javaScriptTracer" };

        yield return new TestCaseData(
            transferTransaction,
            new GethTraceOptions { Tracer = Native4ByteTracer.FourByteTracer },
            """{"jsonrpc":"2.0","result":{},"id":67}"""
        )
        { TestName = "Transfer with " + Native4ByteTracer.FourByteTracer };

        yield return new TestCaseData(
            transferTransaction,
            new GethTraceOptions { Tracer = NativeCallTracer.CallTracer },
            """
            {
                "jsonrpc": "2.0",
                "result": {
                    "type": "CALL",
                    "from": "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099",
                    "to": "0x0000000000000000000000000000000000000000",
                    "value": "0x1",
                    "gas": "0x5208",
                    "gasUsed": "0x5208",
                    "input": "0x"
                },
                "id": 67
            }
            """
        )
        { TestName = "Transfer with " + NativeCallTracer.CallTracer };

        yield return new TestCaseData(
            transferTransaction,
            new GethTraceOptions { Tracer = NativePrestateTracer.PrestateTracer },
            """
            {
                "jsonrpc": "2.0",
                "result": {
                    "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099": {
                        "balance": "0x3635c9adc5de9f09e5",
                        "nonce": 3,
                        "code": "0xabcd"
                    },
                    "0x0000000000000000000000000000000000000000": {
                        "balance": "0x0"
                    },
                    "0x475674cb523a0a2736b7f7534390288fce16982c": {
                        "balance": "0xf618"
                    }
                },
                "id": 67
            }
            """
        )
        { TestName = "Transfer with " + NativePrestateTracer.PrestateTracer };
    }

    private static IEnumerable<TestCaseData> TraceTransactionContractSource()
    {
        var code = Prepare.EvmCode
            .PushData(0)
            .PushData(32)
            .Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Done;

        var contractTransaction = (TestRpcBlockchain b) => Build.A.Transaction
            .WithNonce(b.ReadOnlyState.GetNonce(TestItem.AddressA))
            .WithCode(code)
            .WithGasLimit(100000)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        yield return new TestCaseData(
            contractTransaction,
            new GethTraceOptions(),
            """
            {
                "jsonrpc": "2.0",
                "result": {
                    "gas": 55278,
                    "failed": false,
                    "returnValue": "0x",
                    "structLogs": [
                        {
                            "pc": 0,
                            "op": "PUSH1",
                            "gas": 46928,
                            "gasCost": 3,
                            "depth": 1,
                            "error": null,
                            "stack": [],
                            "storage": {}
                        },
                        {
                            "pc": 2,
                            "op": "PUSH1",
                            "gas": 46925,
                            "gasCost": 3,
                            "depth": 1,
                            "error": null,
                            "stack": [
                                "0x0"
                            ],
                            "storage": {}
                        },
                        {
                            "pc": 4,
                            "op": "SSTORE",
                            "gas": 46922,
                            "gasCost": 2200,
                            "depth": 1,
                            "error": null,
                            "stack": [
                                "0x0",
                                "0x20"
                            ],
                            "storage": {}
                        },
                        {
                            "pc": 5,
                            "op": "STOP",
                            "gas": 44722,
                            "gasCost": 0,
                            "depth": 1,
                            "error": null,
                            "stack": [],
                            "storage": {}
                        }
                    ]
                },
                "id": 67
            }
            """
        )
        { TestName = "Contract with blockMemoryTracer" };

        yield return new TestCaseData(
            contractTransaction,
            new GethTraceOptions { Tracer = "{gasUsed: [], step: function(log) { this.gasUsed.push(log.getGas()); }, result: function() { return this.gasUsed; }, fault: function(){}}" },
            """{"jsonrpc":"2.0","result":[46928,46925,46922,44722],"id":67}"""
        )
        { TestName = "Contract with javaScriptTracer" };

        yield return new TestCaseData(
            contractTransaction,
            new GethTraceOptions { Tracer = Native4ByteTracer.FourByteTracer },
            """{"jsonrpc":"2.0","result":{"0x60006020-2":1},"id":67}"""
        )
        { TestName = "Contract with " + Native4ByteTracer.FourByteTracer };

        yield return new TestCaseData(
            contractTransaction,
            new GethTraceOptions { Tracer = NativeCallTracer.CallTracer },
            """
            {
                "jsonrpc": "2.0",
                "result": {
                    "type": "CREATE",
                    "from": "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099",
                    "to": "0x0ffd3e46594919c04bcfd4e146203c8255670828",
                    "value": "0x1",
                    "gas": "0x186a0",
                    "gasUsed": "0xd7ee",
                    "input": "0x600060205500"
                },
                "id": 67
            }
            """
        )
        { TestName = "Contract with " + NativeCallTracer.CallTracer };

        yield return new TestCaseData(
            contractTransaction,
            new GethTraceOptions { Tracer = NativePrestateTracer.PrestateTracer },
            """
            {
                "jsonrpc": "2.0",
                "result": {
                    "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099": {
                        "balance": "0x3635c9adc5de9f09e5",
                        "nonce": 3,
                        "code": "0xabcd"
                    },
                    "0x0ffd3e46594919c04bcfd4e146203c8255670828": {
                        "balance": "0x0",
                        "storage": {
                            "0x0000000000000000000000000000000000000000000000000000000000000020": "0x0000000000000000000000000000000000000000000000000000000000000000"
                        }
                    },
                    "0x475674cb523a0a2736b7f7534390288fce16982c": {
                        "balance": "0xf618"
                    }
                },
                "id": 67
            }
            """
        )
        { TestName = "Contract with " + NativePrestateTracer.PrestateTracer };
    }
}
