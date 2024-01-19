// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Tracing.GethStyle;
using NUnit.Framework;
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing.GethStyle.JavaScript;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using Nethermind.Specs.Forks;
using Nethermind.State;

namespace Nethermind.Evm.Test.Tracing;

public class GethLikeJavaScriptTracerTests : VirtualMachineTestsBase
{
    [TestCase("{ result: function(ctx, db) { return null } }", TestName = "fault")]
    [TestCase("{ fault: function(log, db) { } }", TestName = "result")]
    [TestCase("{ fault: function(log, db) { }, result: function(ctx, db) { return null }, enter: function(frame) { } }", TestName = "exit")]
    [TestCase("{ fault: function(log, db) { }, result: function(ctx, db) { return null }, exit: function(frame) { } }", TestName = "enter")]
    public void missing_functions(string tracer)
    {
        Action trace = () => ExecuteBlock(GetTracer(tracer), MStore());
        trace.Should().Throw<ArgumentException>();
    }

    [Test]
    public void log_operations()
    {
        string userTracer = @"{
                    retVal: [],
                    step: function(log, db) { this.retVal.push(log.getPC() + ':' + log.op.toString() + ':' + log.getCost() + ':' + log.getGas() + ':' + log.getRefund()) },
                    fault: function(log, db) { this.retVal.push('FAULT: ' + JSON.stringify(log)) },
                    result: function(ctx, db) { return this.retVal }
                }";
        GethLikeTxTrace traces = ExecuteBlock(
                GetTracer(userTracer),
                MStore(),
                MainnetSpecProvider.CancunActivation)
            .BuildResult().First();
        string[] expectedStrings = { "0:PUSH32:0:79000:0", "33:PUSH1:0:78997:0", "35:MSTORE:0:78994:0", "36:PUSH32:0:78988:0", "69:PUSH1:0:78985:0", "71:MSTORE:0:78982:0", "72:STOP:0:78976:0" };
        traces.CustomTracerResult?.Value.Should().BeEquivalentTo(expectedStrings);
    }

    private GethLikeBlockJavaScriptTracer GetTracer(string userTracer) => new(TestState, Shanghai.Instance, GethTraceOptions.Default with { EnableMemory = true, Tracer = userTracer });


    [Test]
    public void log_operation_functions()
    {
        string userTracer = @"{
                    retVal: [],
                    step: function(log, db) { this.retVal.push(log.op.toString() + ' : ' + log.op.toNumber() + ' : ' + log.op.isPush() ) },
                    fault: function(log, db) { this.retVal.push('FAULT: ' + JSON.stringify(log)) },
                    result: function(ctx, db) { return this.retVal }
                }";
        GethLikeTxTrace traces = ExecuteBlock(
                GetTracer(userTracer),
                MStore(),
                MainnetSpecProvider.CancunActivation)
            .BuildResult().First();
        string[] expectedStrings = { "PUSH32 : 127 : true", "PUSH1 : 96 : true", "MSTORE : 82 : false", "PUSH32 : 127 : true", "PUSH1 : 96 : true", "MSTORE : 82 : false", "STOP : 0 : false" };
        traces.CustomTracerResult?.Value.Should().BeEquivalentTo(expectedStrings);
    }

    [Test]
    public void log_stack_functions()
    {
        string userTracer = @"{
                    retVal: [],
                    step: function(log, db) { this.retVal.push(log.stack.length()) },
                    fault: function(log, db) { this.retVal.push('FAULT: ' + JSON.stringify(log)) },
                    result: function(ctx, db) { return this.retVal }
                }";
        GethLikeTxTrace traces = ExecuteBlock(
                GetTracer(userTracer),
                MStore(),
                MainnetSpecProvider.CancunActivation)
            .BuildResult().First();
        int[] expected = { 0, 1, 2, 0, 1, 2, 0 };
        traces.CustomTracerResult?.Value.Should().BeEquivalentTo(expected);
    }

    [Test]
    public void log_memory_functions()
    {
        string userTracer = @"{
                    retVal: [],
                         step: function(log, db) {
                        if (log.op.toNumber() == 0x52) {
                            this.retVal.push(log.memory.length());
                        } else if (log.op.toNumber() == 0x00) {
                            this.retVal.push(log.memory.length());
                        }
                    },
                    fault: function(log, db) { this.retVal.push('FAULT: ' + JSON.stringify(log.getError())) },
                    result: function(ctx, db) { return this.retVal }
                }";
        GethLikeTxTrace traces = ExecuteBlock(
                GetTracer(userTracer),
                MStore(),
                MainnetSpecProvider.CancunActivation)
            .BuildResult().First();
        int[] expectedResult = { 0, 32, 64 };
        traces.CustomTracerResult?.Value.Should().BeEquivalentTo(expectedResult);
    }

    [Test]
    public void log_contract_functions()
    {
        string userTracer = @"{
                    retVal: '',
                    step: function(log, db) { this.retVal = toHex(log.contract.getAddress()) + ':' + toHex(log.contract.getCaller()) + ':' + toHex(log.contract.getInput()) },
                    fault: function(log, db) { this.retVal.push('FAULT: ' + JSON.stringify(log)) },
                    result: function(ctx, db) { return this.retVal }
                }";
        GethLikeTxTrace traces = ExecuteBlock(
                GetTracer(userTracer),
                MStore(),
                MainnetSpecProvider.CancunActivation)
            .BuildResult().First();
        traces.CustomTracerResult?.Value.Should().BeEquivalentTo("942921b14f1b1c385cd7e0cc2ef7abe5598c8358:b7705ae4c6f81b66cdb323c65f4e8133690fc099:");
    }

    [Test]
    public void Js_traces_simple_filter()
    {
        string userTracer = @"{
                                retVal: [],
                                step: function(log, db) { this.retVal.push(log.getPC() + ':' + log.op.toString()) },
                                fault: function(log, db) { this.retVal.push('FAULT: ' + JSON.stringify(log)) },
                                result: function(ctx, db) { return this.retVal }
                            }";
        ;

        GethLikeTxTrace traces = ExecuteBlock(
                GetTracer(userTracer),
                MStore(),
                MainnetSpecProvider.CancunActivation)
            .BuildResult().First();
        string[] expectedStrings = { "0:PUSH32", "33:PUSH1", "35:MSTORE", "36:PUSH32", "69:PUSH1", "71:MSTORE", "72:STOP" };
        Assert.That(traces.CustomTracerResult?.Value, Is.EqualTo(expectedStrings));
    }

    [Test]
    public void filter_with_conditionals()
    {
        string userTracer = @"{
                    retVal: [],
                    step: function(log, db) {
                        if (log.op.toNumber() == 0x60) {
                            this.retVal.push(log.getPC() + ': PUSH1');
                        } else if (log.op.toNumber() == 0x52) {
                            this.retVal.push(log.getPC() + ': MSTORE');
                        }
                    },
                    fault: function(log, db) { this.retVal.push('FAULT: ' + JSON.stringify(log)); },
                    result: function(ctx, db) { return this.retVal; }
                }";
        GethLikeTxTrace traces = ExecuteBlock(
                GetTracer(userTracer),
                MStore(),
                MainnetSpecProvider.CancunActivation)
            .BuildResult().First();
        string[] expectedStrings = { "33: PUSH1", "35: MSTORE", "69: PUSH1", "71: MSTORE" };
        Assert.That(traces.CustomTracerResult?.Value, Is.EqualTo(expectedStrings));
    }

    [Test]
    public void storage_information()
    {
        string userTracer = @"{
                    retVal: [],
                    step: function(log, db) {
                        if (log.op.toNumber() == 0x55)
                            this.retVal.push(log.getPC() + ': SSTORE ' + log.stack.peek(0).toString(16));
                        if (log.op.toNumber() == 0x54)
                            this.retVal.push(log.getPC() + ': SLOAD ' + log.stack.peek(0).toString(16));
                        if (log.op.toNumber() == 0x00)
                            this.retVal.push(log.getPC() + ': STOP ' + log.stack.peek(0).toString(16) + ' <- ' + log.stack.peek(1).toString(16));
                    },
                    fault: function(log, db) {
                        this.retVal.push('FAULT: ' + JSON.stringify(log));
                    },
                    result: function(ctx, db) {
                        return this.retVal;
                    }
                }";
        GethLikeTxTrace traces = ExecuteBlock(
                GetTracer(userTracer),
                SStore_double(),
                MainnetSpecProvider.CancunActivation)
            .BuildResult().First();
        string[] expectedStrings = { "35: SSTORE 0", "71: SSTORE 20", "107: SLOAD 0", "108: STOP a01234 <- a01234" };
        Assert.That(traces.CustomTracerResult?.Value, Is.EqualTo(expectedStrings));
    }

    [Test]
    public void operation_results()
    {
        string userTracer = """
                            {
                                retVal: [],
                                afterSload: false,
                                step: function(log, db) {
                                    if (this.afterSload) {
                                            this.retVal.push("Result: " + log.stack.peek(0).toString(16));
                                        this.afterSload = false;
                                    }
                                    if (log.op.toNumber() == 0x54) {
                                            this.retVal.push(log.getPC() + " SLOAD " + log.stack.peek(0).toString(16));
                                        this.afterSload = true;
                                    }
                                    if (log.op.toNumber() == 0x55)
                                        this.retVal.push(log.getPC() + " SSTORE " + log.stack.peek(0).toString(16) + " <- " + log.stack.peek(1).toString(16));
                                },
                                fault: function(log, db) {
                                    this.retVal.push("FAULT: " + JSON.stringify(log));
                                },
                                result: function(ctx, db) {
                                    return this.retVal;
                                }
                            }
                            """;
        GethLikeTxTrace traces = ExecuteBlock(
                GetTracer(userTracer),
                SStore(),
                MainnetSpecProvider.CancunActivation)
            .BuildResult().First();
        string[] expectedStrings = { "68 SSTORE 1 <- a01234", "104 SLOAD 1", "Result: a01234" };
        Assert.That(traces.CustomTracerResult?.Value, Is.EqualTo(expectedStrings));
    }

    [Test]
    public void calls_btn_contracts()
    {
        string userTracer = """
                            {
                                retVal: [],
                                afterSload: false,
                                callStack: [],
                                byte2Hex: function(byte) {
                                    if (byte < 0x10) {
                                        return "0" + byte.toString(16);
                                    }
                                    return byte.toString(16);
                                },
                                array2Hex: function(arr) {
                                    var retVal = "";
                                    for (var i=0; i<arr.length; i++) {
                                        retVal += this.byte2Hex(arr[i]);
                                    }
                                    return retVal;
                                },
                                getAddr: function(log) {
                                    return this.array2Hex(log.contract.getAddress());
                                },
                                step: function(log, db) {
                                    var opcode = log.op.toNumber();
                                    // SLOAD
                                    if (opcode == 0x54) {
                                        this.retVal.push(log.getPC() + ": SLOAD " +
                                            this.getAddr(log) + ":" +
                                            log.stack.peek(0).toString(16));
                                        this.afterSload = true;
                                    }
                                    // SLOAD Result
                                    if (this.afterSload) {
                                        this.retVal.push("Result: " +
                                            log.stack.peek(0).toString(16));
                                        this.afterSload = false;
                                    }
                                    // SSTORE
                                    if (opcode == 0x55) {
                                        this.retVal.push(log.getPC() + ": SSTORE " +
                                            this.getAddr(log) + ":" +
                                            log.stack.peek(0).toString(16) + " <- " +
                                            log.stack.peek(1).toString(16));
                                    }
                                    // End of step

                                },
                                fault: function(log, db) {
                                    this.retVal.push("FAULT: " + JSON.stringify(log));
                                },
                                result: function(ctx, db) {
                                    return this.retVal;
                            }
                        }
                        """;

        GethLikeTxTrace traces = ExecuteBlock(
                GetTracer(userTracer),
                SStore(),
                MainnetSpecProvider.CancunActivation)
            .BuildResult().First();
        string[] expectedStrings = { "68: SSTORE 942921b14f1b1c385cd7e0cc2ef7abe5598c8358:1 <- a01234", "104: SLOAD 942921b14f1b1c385cd7e0cc2ef7abe5598c8358:1", "Result: 1" };
        Assert.That(traces.CustomTracerResult?.Value, Is.EqualTo(expectedStrings));
    }

    [Test]
    public void noop_tracer_legacy()
    {
        GethLikeTxTrace traces = ExecuteBlock(
                GetTracer("noopTracer"),
                MStore(),
                MainnetSpecProvider.CancunActivation)
            .BuildResult().First();
        Assert.That(traces.CustomTracerResult?.Value, Has.All.Empty);
    }

    [Test]
    public void opcount_tracer()
    {
        GethLikeTxTrace traces = ExecuteBlock(
                GetTracer("opcountTracer"),
                MStore(),
                MainnetSpecProvider.CancunActivation)
            .BuildResult().First();
        Assert.That(traces.CustomTracerResult?.Value, Is.EqualTo(7));
    }

    [Test]
    public void prestate_tracer()
    {
        GethLikeTxTrace traces = ExecuteBlock(
                GetTracer("prestateTracer"),
                NestedCalls(),
                MainnetSpecProvider.CancunActivation)
            .BuildResult().First();

        Assert.That(JsonSerializer.Serialize(traces.CustomTracerResult?.Value), Is.EqualTo("{\"942921b14f1b1c385cd7e0cc2ef7abe5598c8358\":{\"balance\":\"0x56bc75e2d63100000\",\"nonce\":0,\"code\":\"60006000600060007376e68a8696537e4141926f3e528733af9e237d6961c350f400\",\"storage\":{}},\"76e68a8696537e4141926f3e528733af9e237d69\":{\"balance\":\"0xde0b6b3a7640000\",\"nonce\":0,\"code\":\"7f7f000000000000000000000000000000000000000000000000000000000000006000527f0060005260036000f30000000000000000000000000000000000000000000000602052602960006000f000\",\"storage\":{}},\"89aa9b2ce05aaef815f25b237238c0b4ffff6ae3\":{\"balance\":\"0x0\",\"nonce\":0,\"code\":\"\",\"storage\":{}},\"b7705ae4c6f81b66cdb323c65f4e8133690fc099\":{\"balance\":\"0x56bc75e2d63100000\",\"nonce\":0,\"code\":\"\",\"storage\":{}}}"));
    }

    [Test]
    public void call_tracer()
    {
        GethLikeTxTrace traces = ExecuteBlock(
                GetTracer("callTracer"),
                NestedCalls(),
                MainnetSpecProvider.CancunActivation)
            .BuildResult().First();
    }

    [Test]
    public void multiple_prestate_tracer([Values(10)] int count)
    {
        for (int i = 0; i < count; i++)
        {
            calls_btn_contracts();
        }
    }

    private static byte[] MStore()
    {
        return Prepare.EvmCode
            .PushData(SampleHexData1.PadLeft(64, '0'))
            .PushData(0)
            .Op(Instruction.MSTORE)
            .PushData(SampleHexData2.PadLeft(64, '0'))
            .PushData(32)
            .Op(Instruction.MSTORE)
            .Op(Instruction.STOP)
            .Done;
    }

    private static byte[] SStore_double()
    {
        return Prepare.EvmCode
            .PushData(SampleHexData1.PadLeft(64, '0'))
            .PushData(0)
            .Op(Instruction.SSTORE)
            .PushData(SampleHexData2.PadLeft(64, '0'))
            .PushData(32)
            .Op(Instruction.SSTORE)
            .PushData(SampleHexData1.PadLeft(64, '0'))
            .PushData(0)
            .Op(Instruction.SLOAD)
            .Op(Instruction.STOP)
            .Done;
    }

    private static byte[] SStore()
    {
        return Prepare.EvmCode
            .PushData(SampleHexData2.PadLeft(64, '0'))
            .PushData(SampleHexData1.PadLeft(64, '0'))
            .PushData(UInt256.One)
            .Op(Instruction.SSTORE)
            .PushData(SampleHexData1.PadLeft(64, '0'))
            .PushData(UInt256.One)
            .Op(Instruction.SLOAD)
            .Op(Instruction.STOP)
            .Done;
    }

    private byte[] NestedCalls()
    {
        byte[] deployedCode = new byte[3];

        byte[] initCode = Prepare.EvmCode
            .ForInitOf(deployedCode)
            .Done;

        byte[] createCode = Prepare.EvmCode
            .Create(initCode, 0)
            .Op(Instruction.STOP)
            .Done;

        TestState.CreateAccount(TestItem.AddressC, 1.Ether());
        TestState.InsertCode(TestItem.AddressC, createCode, Spec);
        return Prepare.EvmCode
            .DelegateCall(TestItem.AddressC, 50000)
            .Op(Instruction.STOP)
            .Done;
    }

    [Test]
    public void complex_tracer()
    {
        GethLikeTxTrace traces = ExecuteBlock(
                GetTracer(ComplexTracer),
                Array.Empty<byte>(),
                MainnetSpecProvider.CancunActivation)
            .BuildResult().First();

        TestContext.WriteLine(GetEthereumJsonSerializer().Serialize(traces.CustomTracerResult));
    }

    [Test]
    public void complex_tracer_nested_call()
    {
        GethLikeTxTrace traces = ExecuteBlock(
                GetTracer(ComplexTracer),
                NestedCalls(),
                MainnetSpecProvider.CancunActivation)
            .BuildResult().First();

        TestContext.WriteLine(GetEthereumJsonSerializer().Serialize(traces.CustomTracerResult));
    }


    private static EthereumJsonSerializer GetEthereumJsonSerializer() => new();

    private const string ComplexTracer = """
                                         {
                                             trace: [],
                                             randomAddress: Array(19).fill(87).concat([1]),
                                             setup: function(config) {
                                                 this.trace.push(config);
                                                 this.hash = toWord(Array(31).fill(1).concat([1]));
                                                 this.previousStackLength = 0;
                                                 this.previousMemoryLength = 0;
                                             },
                                             enter: function(callFrame) {
                                                 this.trace.push({
                                                     "type": callFrame.getType(),
                                                     "from": callFrame.getFrom(),
                                                     "to": callFrame.getTo(),
                                                     "input": callFrame.getInput(),
                                                     "gas": callFrame.getGas(),
                                                     "value": callFrame.getValue()
                                                 });
                                             },
                                             exit: function(frameResult) {
                                                 this.trace.push({
                                                     "gasUsed": frameResult.getGasUsed(),
                                                     "output": frameResult.getOutput(),
                                                     "error": frameResult.getError()
                                                 });
                                             },
                                             step: function(log, db) {
                                                 if (log.getError() === undefined) {
                                                     let contractAddress = log.contract.getAddress();
                                                     let currentStackLength = log.stack.length();
                                                     let topStackItem = currentStackLength > 0 ? log.stack.peek(0) : 0;
                                                     let topStackItemValueOf = currentStackLength > 0 ? log.stack.peek(0).valueOf() : 0;
                                                     let topStackItemToString = currentStackLength > 0 ? log.stack.peek(0).toString(16) : 0;
                                                     let bottomStackItem = currentStackLength > 0 ? log.stack.peek(currentStackLength - 1) : 0;
                                                     let currentMemoryLength = log.memory.length();
                                                     let memoryExpanded = currentMemoryLength > this.previousMemoryLength;
                                                     let newMemorySlice = memoryExpanded ? log.memory.slice(Math.max(this.previousMemoryLength, currentMemoryLength - 10), currentMemoryLength) : [];
                                                     let newMemoryItem = memoryExpanded && currentMemoryLength >= 32 ? log.memory.getUint(currentMemoryLength - 32) : 0;
                                                     this.trace.push({
                                                         "op": {
                                                             "isPush": log.op.isPush(),
                                                             "asString": log.op.toString(),
                                                             "asNumber": log.op.toNumber()
                                                         },
                                                         "stack": {
                                                             "top": topStackItem,
                                                             "topValueOf": topStackItemValueOf,
                                                             "topToString": topStackItemToString,
                                                             "bottom": bottomStackItem,
                                                             "length": currentStackLength
                                                         },
                                                         "memory": {
                                                             "newSlice": newMemorySlice,
                                                             "newMemoryItem": newMemoryItem,
                                                             "length": currentMemoryLength
                                                         },
                                                         "contract": {
                                                             "caller": log.contract.getCaller(),
                                                             "address": toAddress(toHex(contractAddress)),
                                                             "value": log.contract.getValue(),
                                                             "input": log.contract.getInput(),
                                                             "balance": db.getBalance(contractAddress),
                                                             "nonce": db.getNonce(contractAddress),
                                                             "code": db.getCode(contractAddress),
                                                             "state": db.getState(contractAddress, this.hash),
                                                             "stateString": db.getState(contractAddress, this.hash).toString(16),
                                                             "exists": db.exists(contractAddress),
                                                             "randomexists": db.exists(this.randomAddress)
                                                         },
                                                         "pc": log.getPC(),
                                                         "gas": log.getGas(),
                                                         "cost": log.getCost(),
                                                         "depth": log.getDepth(),
                                                         "refund": log.getRefund()
                                                     });
                                                     this.previousStackLength = currentStackLength;
                                                     this.previousMemoryLength = currentMemoryLength;
                                                 }
                                                 else {
                                                     this.trace.push({"error": log.getError()});
                                                 }
                                             },
                                             result: function(ctx, db) {
                                                 let ctxToAddress = toAddress(toHex(ctx.to));
                                                 this.trace.push({
                                                     "ctx": {
                                                         "type": ctx.type,
                                                         "from": ctx.from,
                                                         "to": ctx.to,
                                                         "input": ctx.input,
                                                         "gas": ctx.gas,
                                                         "gasUsed": ctx.gasUsed,
                                                         "gasPrice": ctx.gasPrice,
                                                         "value": ctx.value,
                                                         "block": ctx.block,
                                                         "output": ctx.output,
                                                         "error": ctx.error
                                                     },
                                                     "db": {
                                                         "balance": db.getBalance(ctxToAddress),
                                                         "nonce": db.getNonce(ctxToAddress),
                                                         "code": db.getCode(ctxToAddress),
                                                         "state": db.getState(ctxToAddress, this.hash),
                                                         "exists": db.exists(ctxToAddress),
                                                         "randomexists": db.exists(this.randomAddress)
                                                     }
                                                 });
                                                 return this.trace;
                                             },
                                             fault: this.step
                                         }
                                         """;
}
