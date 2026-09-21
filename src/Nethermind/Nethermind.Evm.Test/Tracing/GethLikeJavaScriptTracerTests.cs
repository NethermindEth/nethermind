// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ClearScript;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Blockchain.Tracing.GethStyle;
using NUnit.Framework;
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using Nethermind.Specs.Forks;
using Nethermind.Evm.State;

namespace Nethermind.Evm.Test.Tracing;

using Nethermind.Blockchain.Tracing.GethStyle.Custom.JavaScript;

public class GethLikeJavaScriptTracerTests : VirtualMachineTestsBase
{
    [Test]
    public void Concurrent_custom_tracer_compilation_keeps_cached_script_alive()
    {
        const int concurrency = 16;
        string marker = Guid.NewGuid().ToString("N");
        string tracerCode = $$"""
                              {
                                  marker: "{{marker}}",
                                  result: function() { return this.marker; }
                              }
                              """;
        using CountdownEvent ready = new(concurrency);
        using ManualResetEventSlim start = new();

        Task[] tasks = Enumerable.Range(0, concurrency).Select(_ => Task.Factory.StartNew(
            () =>
            {
                using Engine engine = new(Shanghai.Instance);
                ready.Signal();
                start.Wait();
                dynamic tracer = engine.CreateTracer(tracerCode);
                Assert.That((string)tracer.result(), Is.EqualTo(marker));
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default)).ToArray();

        bool allReady = ready.Wait(TimeSpan.FromSeconds(30));
        start.Set();
        Assert.That(allReady, Is.True);
        Task.WhenAll(tasks).GetAwaiter().GetResult();

        using Engine sequentialEngine = new(Shanghai.Instance);
        dynamic cachedTracer = sequentialEngine.CreateTracer(tracerCode);
        Assert.That((string)cachedTracer.result(), Is.EqualTo(marker));
    }

    [TestCase("{ result: function(ctx, db) { return null } }", TestName = "fault")]
    [TestCase("{ fault: function(log, db) { } }", TestName = "result")]
    [TestCase("{ fault: function(log, db) { }, result: function(ctx, db) { return null }, enter: function(frame) { } }", TestName = "exit")]
    [TestCase("{ fault: function(log, db) { }, result: function(ctx, db) { return null }, exit: function(frame) { } }", TestName = "enter")]
    public void missing_functions(string tracerCode)
    {
        using GethLikeBlockJavaScriptTracer tracer = GetTracer(tracerCode);
        Action trace = () => ExecuteBlock(tracer, MStore());
        Assert.That(trace, Throws.TypeOf<ArgumentException>());
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
        using GethLikeBlockJavaScriptTracer tracer = ExecuteBlock(
                GetTracer(userTracer),
                MStore(),
                MainnetSpecProvider.CancunActivation);
        using GethLikeTxTrace traces = tracer.BuildResult().First();
        string[] expectedStrings = { "0:PUSH32:0:79000:0", "33:PUSH1:0:78997:0", "35:MSTORE:0:78994:0", "36:PUSH32:0:78988:0", "69:PUSH1:0:78985:0", "71:MSTORE:0:78982:0", "72:STOP:0:78976:0" };
        AssertResult(traces, expectedStrings);
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
        using GethLikeBlockJavaScriptTracer tracer = ExecuteBlock(
                GetTracer(userTracer),
                MStore(),
                MainnetSpecProvider.CancunActivation);
        using GethLikeTxTrace traces = tracer.BuildResult().First();
        string[] expectedStrings = { "PUSH32 : 127 : true", "PUSH1 : 96 : true", "MSTORE : 82 : false", "PUSH32 : 127 : true", "PUSH1 : 96 : true", "MSTORE : 82 : false", "STOP : 0 : false" };
        AssertResult(traces, expectedStrings);
    }

    [TestCase(Instruction.PREVRANDAO, "DIFFICULTY")]
    [TestCase((Instruction)0x0f, "opcode 0xf not defined")]
    public void Log_opcode_to_string_uses_geth_names(Instruction instruction, string expected)
    {
        Log.Opcode opcode = new(instruction);

        Assert.That(opcode.toString(), Is.EqualTo(expected));
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
        using GethLikeBlockJavaScriptTracer tracer = ExecuteBlock(
                GetTracer(userTracer),
                MStore(),
                MainnetSpecProvider.CancunActivation);
        using GethLikeTxTrace traces = tracer.BuildResult().First();
        int[] expected = { 0, 1, 2, 0, 1, 2, 0 };
        AssertResult(traces, expected);
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
        using GethLikeBlockJavaScriptTracer tracer = ExecuteBlock(
                GetTracer(userTracer),
                MStore(),
                MainnetSpecProvider.CancunActivation);
        using GethLikeTxTrace traces = tracer.BuildResult().First();
        int[] expectedResult = { 0, 32, 64 };
        AssertResult(traces, expectedResult);
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
        using GethLikeBlockJavaScriptTracer tracer = ExecuteBlock(
                GetTracer(userTracer),
                MStore(),
                MainnetSpecProvider.CancunActivation);
        using GethLikeTxTrace traces = tracer.BuildResult().First();
        AssertResult(traces, "942921b14f1b1c385cd7e0cc2ef7abe5598c8358:b7705ae4c6f81b66cdb323c65f4e8133690fc099:");
    }

    [Test]
    public void log_contract_is_restored_after_inner_call_returns()
    {
        string userTracer = @"{
                    retVal: [],
                    step: function(log, db) {
                        var entry = log.getDepth() + ':' + toHex(log.contract.getAddress());
                        if (this.retVal.length == 0 || this.retVal[this.retVal.length - 1] != entry) {
                            this.retVal.push(entry);
                        }
                    },
                    fault: function(log, db) { },
                    result: function(ctx, db) { return this.retVal }
                }";
        TestState.CreateAccount(TestItem.AddressC, 1.Ether);
        TestState.InsertCode(TestItem.AddressC, Prepare.EvmCode.Op(Instruction.STOP).Done, Spec);
        byte[] code = Prepare.EvmCode
            .Call(TestItem.AddressC, 50000)
            .Op(Instruction.STOP)
            .Done;
        using GethLikeBlockJavaScriptTracer tracer = ExecuteBlock(
                GetTracer(userTracer),
                code,
                MainnetSpecProvider.CancunActivation);
        using GethLikeTxTrace traces = tracer.BuildResult().First();
        string caller = "1:942921b14f1b1c385cd7e0cc2ef7abe5598c8358";
        string callee = "2:76e68a8696537e4141926f3e528733af9e237d69";
        AssertResult(traces, new[] { caller, callee, caller });
    }

    [Test]
    public void post_step_fires_once_per_step()
    {
        // postStep runs from ReportOperationRemainingGas. The interpreter reports that once per
        // instruction, and the CALL handler reports it once more itself before the child frame runs, so
        // the one CALL below is the only step that fires postStep twice. Both frames halt on an explicit
        // opcode, so no instruction picks up the extra end-of-code report either.
        string userTracer = @"{
                    steps: 0,
                    postSteps: 0,
                    step: function(log, db) { this.steps++ },
                    postStep: function(log, db) { this.postSteps++ },
                    fault: function(log, db) { },
                    result: function(ctx, db) { return this.steps + ':' + this.postSteps }
                }";
        TestState.CreateAccount(TestItem.AddressC, 1.Ether);
        TestState.InsertCode(TestItem.AddressC, Prepare.EvmCode
            .PushData(0)
            .PushData(0)
            .Op(Instruction.RETURN)
            .Done, Spec);
        byte[] code = Prepare.EvmCode
            .Call(TestItem.AddressC, 50000)
            .Op(Instruction.STOP)
            .Done;

        using GethLikeBlockJavaScriptTracer tracer = ExecuteBlock(
                GetTracer(userTracer),
                code,
                MainnetSpecProvider.CancunActivation);
        using GethLikeTxTrace traces = tracer.BuildResult().First();

        string[] counts = ResultJson(traces).Trim('"').Split(':');
        int steps = int.Parse(counts[0]);
        Assert.That(steps, Is.GreaterThan(0));
        Assert.That(int.Parse(counts[1]), Is.EqualTo(steps + 1), "postStep must fire once per step, plus the CALL's own report");
    }

    private static IEnumerable<TestCaseData> InstructionCallbackCases()
    {
        yield return new TestCaseData("5f5f20", "0:PUSH0,1:PUSH0,2:KECCAK256,3:STOP", 0)
            .SetName($"{nameof(Instruction_callbacks_are_paired)}_Terminal_continuable_opcode");
        yield return new TestCaseData("5f5f205000", "0:PUSH0,1:PUSH0,2:KECCAK256,3:POP,4:STOP", 0)
            .SetName($"{nameof(Instruction_callbacks_are_paired)}_Non_terminal_continuable_opcode");
        yield return new TestCaseData("00", "0:STOP", 0)
            .SetName($"{nameof(Instruction_callbacks_are_paired)}_Explicit_stop");
        yield return new TestCaseData("5f5ff3", "0:PUSH0,1:PUSH0,2:RETURN", 0)
            .SetName($"{nameof(Instruction_callbacks_are_paired)}_Explicit_return");
        yield return new TestCaseData("5f5ffd", "0:PUSH0,1:PUSH0,2:REVERT", 1)
            .SetName($"{nameof(Instruction_callbacks_are_paired)}_Explicit_revert");
        yield return new TestCaseData("5fff", "0:PUSH0,1:SELFDESTRUCT", 0)
            .SetName($"{nameof(Instruction_callbacks_are_paired)}_Explicit_self_destruct");
        yield return new TestCaseData("20", "0:KECCAK256", 1)
            .SetName($"{nameof(Instruction_callbacks_are_paired)}_Stack_underflow");
        yield return new TestCaseData("63ffffffff5f20", "0:PUSH4,5:PUSH0,6:KECCAK256", 1)
            .SetName($"{nameof(Instruction_callbacks_are_paired)}_Out_of_gas");
        yield return new TestCaseData("5f5f57", "0:PUSH0,1:PUSH0,2:JUMPI,3:STOP", 0)
            .SetName($"{nameof(Instruction_callbacks_are_paired)}_Terminal_jump_if");
    }

    [TestCaseSource(nameof(InstructionCallbackCases))]
    public void Instruction_callbacks_are_paired(string codeHex, string operations, int faults)
    {
        string userTracer = @"{
                    steps: [],
                    postSteps: [],
                    faults: 0,
                    step: function(log, db) { this.steps.push(log.getPC() + ':' + log.op.toString()) },
                    postStep: function(log, db) { this.postSteps.push(log.getPC() + ':' + log.op.toString()) },
                    fault: function(log, db) { this.faults++ },
                    result: function(ctx, db) { return this.steps.join(',') + '|' + this.postSteps.join(',') + '|' + this.faults }
                }";

        using GethLikeBlockJavaScriptTracer tracer = ExecuteBlock(
            GetTracer(userTracer),
            Bytes.FromHexString(codeHex),
            MainnetSpecProvider.CancunActivation);
        using GethLikeTxTrace traces = tracer.BuildResult().First();

        AssertResult(traces, $"{operations}|{operations}|{faults}");
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

        using GethLikeBlockJavaScriptTracer tracer = ExecuteBlock(
                GetTracer(userTracer),
                MStore(),
                MainnetSpecProvider.CancunActivation);
        using GethLikeTxTrace traces = tracer.BuildResult().First();
        string[] expectedStrings = { "0:PUSH32", "33:PUSH1", "35:MSTORE", "36:PUSH32", "69:PUSH1", "71:MSTORE", "72:STOP" };
        AssertResult(traces, expectedStrings);
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
        using GethLikeBlockJavaScriptTracer tracer = ExecuteBlock(
                GetTracer(userTracer),
                MStore(),
                MainnetSpecProvider.CancunActivation);
        using GethLikeTxTrace traces = tracer.BuildResult().First();
        string[] expectedStrings = { "33: PUSH1", "35: MSTORE", "69: PUSH1", "71: MSTORE" };
        AssertResult(traces, expectedStrings);
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
        using GethLikeBlockJavaScriptTracer tracer = ExecuteBlock(
                GetTracer(userTracer),
                SStore_double(),
                MainnetSpecProvider.CancunActivation);
        using GethLikeTxTrace traces = tracer.BuildResult().First();
        string[] expectedStrings = { "35: SSTORE 0", "71: SSTORE 20", "107: SLOAD 0", "108: STOP a01234 <- a01234" };
        AssertResult(traces, expectedStrings);
    }

    [Test]
    public void getState_reads_live_slot_by_raw_key()
    {
        string userTracer = @"{
                    retVal: [],
                    step: function(log, db) {
                        if (log.op.toNumber() == 0x00) {
                            let a = log.contract.getAddress();
                            this.retVal.push(toHex(db.getState(a, toWord('0'))));
                            this.retVal.push(toHex(db.getState(a, toWord('20'))));
                        }
                    },
                    fault: function(log, db) { },
                    result: function(ctx, db) {
                        return this.retVal;
                    }
                }";
        using GethLikeBlockJavaScriptTracer tracer = ExecuteBlock(
                GetTracer(userTracer),
                SStore_double(),
                MainnetSpecProvider.CancunActivation);
        using GethLikeTxTrace traces = tracer.BuildResult().First();
        string[] expectedStrings = { SampleHexData1.PadLeft(64, '0'), SampleHexData2.PadLeft(64, '0') };
        AssertResult(traces, expectedStrings);
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
        using GethLikeBlockJavaScriptTracer tracer = ExecuteBlock(
                GetTracer(userTracer),
                SStore(),
                MainnetSpecProvider.CancunActivation);
        using GethLikeTxTrace traces = tracer.BuildResult().First();
        string[] expectedStrings = { "68 SSTORE 1 <- a01234", "104 SLOAD 1", "Result: a01234" };
        AssertResult(traces, expectedStrings);
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

        using GethLikeBlockJavaScriptTracer tracer = ExecuteBlock(
                GetTracer(userTracer),
                SStore(),
                MainnetSpecProvider.CancunActivation);
        using GethLikeTxTrace traces = tracer.BuildResult().First();
        string[] expectedStrings = { "68: SSTORE 942921b14f1b1c385cd7e0cc2ef7abe5598c8358:1 <- a01234", "104: SLOAD 942921b14f1b1c385cd7e0cc2ef7abe5598c8358:1", "Result: 1" };
        AssertResult(traces, expectedStrings);
    }

    [Test]
    public void noop_tracer_legacy()
    {
        using GethLikeBlockJavaScriptTracer tracer = ExecuteBlock(
                GetTracer("noopTracer_legacy"),
                MStore(),
                MainnetSpecProvider.CancunActivation);
        using GethLikeTxTrace traces = tracer.BuildResult().First();
        AssertResult(traces, new { });
    }

    [Test]
    public void opcount_tracer()
    {
        using GethLikeBlockJavaScriptTracer tracer = ExecuteBlock(
                GetTracer("opcountTracer"),
                MStore(),
                MainnetSpecProvider.CancunActivation);
        using GethLikeTxTrace traces = tracer.BuildResult().First();
        AssertResult(traces, 7);
    }

    [Test]
    public void prestate_tracer()
    {
        using GethLikeBlockJavaScriptTracer tracer = ExecuteBlock(
                GetTracer("prestateTracer_legacy"),
                NestedCalls(),
                MainnetSpecProvider.CancunActivation);
        using GethLikeTxTrace traces = tracer.BuildResult().First();

        Assert.That(JsonSerializer.Serialize(traces.CustomTracerResult?.Value), Is.EqualTo("{\"942921b14f1b1c385cd7e0cc2ef7abe5598c8358\":{\"balance\":\"0x56bc75e2d63100000\",\"nonce\":0,\"code\":\"60006000600060007376e68a8696537e4141926f3e528733af9e237d6961c350f400\",\"storage\":{}},\"76e68a8696537e4141926f3e528733af9e237d69\":{\"balance\":\"0xde0b6b3a7640000\",\"nonce\":0,\"code\":\"7f7f000000000000000000000000000000000000000000000000000000000000006000527f0060005260036000f30000000000000000000000000000000000000000000000602052602960006000f000\",\"storage\":{}},\"89aa9b2ce05aaef815f25b237238c0b4ffff6ae3\":{\"balance\":\"0x0\",\"nonce\":0,\"code\":\"\",\"storage\":{}},\"b7705ae4c6f81b66cdb323c65f4e8133690fc099\":{\"balance\":\"0x56bc75e2d63100000\",\"nonce\":0,\"code\":\"\",\"storage\":{}}}"));
    }

    [Test]
    public void call_tracer()
    {
        using GethLikeBlockJavaScriptTracer tracer = ExecuteBlock(
                GetTracer("callTracer_legacy"),
                NestedCalls(),
                MainnetSpecProvider.CancunActivation);
        using GethLikeTxTrace traces = tracer.BuildResult().First();

        Assert.That(JsonSerializer.Serialize(traces.CustomTracerResult?.Value), Is.EqualTo("{\"type\":\"CALL\",\"from\":\"b7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"to\":\"942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"value\":\"0x1\",\"gas\":\"0x186a0\",\"gasUsed\":\"0xdbd1\",\"input\":\"\",\"output\":\"\",\"calls\":[{\"type\":\"DELEGATECALL\",\"from\":\"942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"to\":\"76e68a8696537e4141926f3e528733af9e237d69\",\"gas\":\"0xc350\",\"gasUsed\":\"0x14d07\",\"input\":\"\",\"output\":\"\",\"calls\":[{\"type\":\"CREATE\",\"from\":\"942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"to\":\"89aa9b2ce05aaef815f25b237238c0b4ffff6ae3\",\"value\":\"0x0\",\"gas\":\"0x4513\",\"gasUsed\":\"0x7f6e\",\"input\":\"7f000000000000000000000000000000000000000000000000000000000000000060005260036000f3\",\"output\":\"000000\"}]}]}"));
    }

    [Test]
    public void _4byte_tracer_legacy()
    {
        using GethLikeBlockJavaScriptTracer tracer = ExecuteBlock(
                GetTracer("4byteTracer_legacy"),
                CallWithInput(),
                MainnetSpecProvider.CancunActivation);
        using GethLikeTxTrace traces = tracer.BuildResult().First();

        Assert.That(JsonSerializer.Serialize(traces.CustomTracerResult?.Value), Is.EqualTo("{\"00000000-1\":2,\"00000000-2\":1}"));
    }

    [Test]
    public void multiple_prestate_tracer([Values(10)] int count)
    {
        for (int i = 0; i < count; i++)
        {
            calls_btn_contracts();
        }
    }

    private static byte[] MStore() => Prepare.EvmCode
                .PushData(SampleHexData1.PadLeft(64, '0'))
                .PushData(0)
                .Op(Instruction.MSTORE)
                .PushData(SampleHexData2.PadLeft(64, '0'))
                .PushData(32)
                .Op(Instruction.MSTORE)
                .Op(Instruction.STOP)
                .Done;

    private static byte[] SStore_double() => Prepare.EvmCode
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

    private static byte[] SStore() => Prepare.EvmCode
            .PushData(SampleHexData2.PadLeft(64, '0'))
            .PushData(SampleHexData1.PadLeft(64, '0'))
            .PushData(UInt256.One)
            .Op(Instruction.SSTORE)
            .PushData(SampleHexData1.PadLeft(64, '0'))
            .PushData(UInt256.One)
            .Op(Instruction.SLOAD)
            .Op(Instruction.STOP)
            .Done;

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

        TestState.CreateAccount(TestItem.AddressC, 1.Ether);
        TestState.InsertCode(TestItem.AddressC, createCode, Spec);
        return Prepare.EvmCode
            .DelegateCall(TestItem.AddressC, 50000)
            .Op(Instruction.STOP)
            .Done;
    }

    private byte[] CallWithInput()
    {
        byte[] input = new byte[5];
        byte[] input2 = new byte[6];

        return Prepare.EvmCode
            .CallWithInput(TestItem.AddressC, 50000, input)
            .CallWithInput(TestItem.AddressC, 50000, input)
            .CallWithInput(TestItem.AddressC, 50000, input2)
            .Done;
    }

    [Test]
    public void complex_tracer()
    {
        using GethLikeBlockJavaScriptTracer tracer = ExecuteBlock(
                GetTracer(ComplexTracer),
                [],
                MainnetSpecProvider.CancunActivation);
        using GethLikeTxTrace traces = tracer.BuildResult().First();

        TestContext.Out.WriteLine(GetEthereumJsonSerializer().Serialize(traces.CustomTracerResult));
    }

    [Test]
    public void complex_tracer_nested_call()
    {
        using GethLikeBlockJavaScriptTracer tracer = ExecuteBlock(
                GetTracer(ComplexTracer),
                NestedCalls(),
                MainnetSpecProvider.CancunActivation);
        using GethLikeTxTrace traces = tracer.BuildResult().First();

        TestContext.Out.WriteLine(GetEthereumJsonSerializer().Serialize(traces.CustomTracerResult));
    }

    [Test]
    public void Completed_results_keep_big_integer_values_after_repeated_disposal()
    {
        const string valueTracer = """
            {
                fault: function(log, db) { },
                result: function(ctx, db) { return { value: ctx.value.add(1).toString(), bytes: toHex(toWord('1')) }; }
            }
            """;

        for (int i = 0; i < 3; i++)
        {
            using Engine engine = new(Shanghai.Instance);
            using GethLikeJavaScriptTxTracer tracer = new(engine, new Db(TestState),
                new Context { Value = UInt256.MaxValue }, GethTraceOptions.Default with { Tracer = valueTracer });
            using GethLikeTxTrace trace = tracer.BuildResult();
            tracer.Dispose();
            tracer.Dispose();

            AssertResult(trace, new
            {
                value = "115792089237316195423570985008687907853269984665640564039457584007913129639936",
                bytes = "0000000000000000000000000000000000000000000000000000000000000001"
            });
        }
    }

    [Test]
    public void Result_failure_does_not_prevent_the_next_trace_from_using_host_helpers()
    {
        const string failingTracer = "{ fault: function() { }, result: function() { throw new Error('result failed'); } }";
        using (GethLikeBlockJavaScriptTracer tracer = GetTracer(failingTracer))
        {
            Assert.That(() => ExecuteBlock(tracer, MStore()), Throws.InstanceOf(typeof(IScriptEngineException)));
        }

        const string recoveringTracer = "{ fault: function() { }, result: function() { return toHex(toWord('1')); } }";
        using GethLikeBlockJavaScriptTracer recovered = ExecuteBlock(GetTracer(recoveringTracer), MStore());
        using GethLikeTxTrace trace = recovered.BuildResult().First();

        AssertResult(trace, "0000000000000000000000000000000000000000000000000000000000000001");
    }

    [Test]
    public void Block_trace_gives_every_transaction_a_fresh_script_scope()
    {
        const string countingTracer = @"{
                    step: function(log, db) { },
                    fault: function(log, db) { },
                    result: function(ctx, db) { globalThis.traced = (globalThis.traced || 0) + 1; return globalThis.traced; }
                }";
        using GethLikeBlockJavaScriptTracer tracer = ExecuteTwoTransactionBlock(GetTracer(countingTracer));

        string[] results = ResultJsons(tracer.BuildResult());

        Assert.That(results, Is.EqualTo(new[] { "1", "1" }));
    }

    [Test]
    public void Disposing_one_transaction_trace_keeps_the_sibling_result_readable()
    {
        using GethLikeBlockJavaScriptTracer tracer = ExecuteTwoTransactionBlock(GetTracer(StepCountingTracer));
        GethLikeTxTrace[] traces = tracer.BuildResult().ToArray();

        traces[0].Dispose();
        using GethLikeTxTrace survivor = traces[1];

        Assert.That(GetEthereumJsonSerializer().Serialize(survivor.CustomTracerResult), Is.EqualTo("""{"steps":7}"""));
    }

    [Test]
    [NonParallelizable]
    public void Heap_limit_violation_fails_the_trace_and_later_traces_recover()
    {
        Action hoardingTrace = () =>
        {
            using GethLikeBlockJavaScriptTracer tracer = GetTracer(HoardingTracer);
            ExecuteBlock(tracer, PushPopSequence(400));
        };

        Assert.That(hoardingTrace, Throws.InstanceOf(typeof(IScriptEngineException)));

        using GethLikeBlockJavaScriptTracer recovered = ExecuteBlock(GetTracer(StepCountingTracer), MStore());
        using GethLikeTxTrace trace = recovered.BuildResult().First();

        Assert.That(GetEthereumJsonSerializer().Serialize(trace.CustomTracerResult), Is.EqualTo("""{"steps":7}"""));
    }

    [Test]
    public void Block_trace_does_not_carry_a_failed_transaction_error_into_the_next()
    {
        const string errorTracer = @"{
                    step: function(log, db) { },
                    fault: function(log, db) { },
                    result: function(ctx, db) { return ctx.error === undefined ? 'none' : 'error'; }
                }";
        byte[] failingCode = Prepare.EvmCode.Op(Instruction.REVERT).Done;
        using GethLikeBlockJavaScriptTracer tracer = ExecuteTwoTransactionBlock(GetTracer(errorTracer), failingCode, MStore());

        string[] results = ResultJsons(tracer.BuildResult());

        Assert.That(results, Is.EqualTo(new[] { "\"error\"", "\"none\"" }));
    }

    [Test]
    [NonParallelizable]
    public void Heap_limit_violation_still_trips_after_an_earlier_transaction_re_armed_it()
    {
        Action hoardingSecondTransaction = () =>
        {
            using GethLikeBlockJavaScriptTracer tracer = GetTracer(HoardingTracer);
            ExecuteTwoTransactionBlock(tracer, MStore(), PushPopSequence(400));
        };

        Assert.That(hoardingSecondTransaction, Throws.InstanceOf(typeof(IScriptEngineException)));
    }

    [Test]
    public void Rendered_result_respects_the_response_serializer_depth_limit()
    {
        const string nestedTracer = @"{
                    step: function(log, db) { },
                    fault: function(log, db) { },
                    result: function(ctx, db) { return { a: { b: { c: { d: 1 } } } }; }
                }";
        using GethLikeBlockJavaScriptTracer tracer = ExecuteBlock(GetTracer(nestedTracer), MStore());
        using GethLikeTxTrace trace = tracer.BuildResult().First();
        JsonSerializerOptions shallow = new(EthereumJsonSerializer.JsonOptions) { MaxDepth = 3 };

        Assert.That(() => JsonSerializer.Serialize(trace.CustomTracerResult, shallow), Throws.InstanceOf<JsonException>());
    }

    [Test]
    [NonParallelizable]
    public void Engine_construction_failing_under_a_pending_violation_leaves_later_engines_usable()
    {
        using (Engine hoarder = new(Shanghai.Instance))
        {
            dynamic hoardingTracer = hoarder.CreateTracer(HoardingTracer);
            try
            {
                Action hoard = () =>
                {
                    for (int i = 0; i < 400; i++)
                    {
                        hoardingTracer.step(null, null);
                    }
                };
                Assert.That(hoard, Throws.InstanceOf(typeof(IScriptEngineException)), "the hoarding script must trip the limit");
                Assert.That(() => new Engine(Shanghai.Instance).Dispose(), Throws.InstanceOf(typeof(IScriptEngineException)), "engines cannot start while the hoard still holds the heap over the limit");
            }
            finally
            {
                ((object)hoardingTracer as IDisposable)?.Dispose();
            }
        }

        using Engine recovered = new(Shanghai.Instance);
        dynamic probe = recovered.CreateTracer("{ result: function(ctx, db) { return 7; } }");

        Assert.That((int)probe.result(), Is.EqualTo(7));
    }

    [Test]
    [NonParallelizable]
    public void Releasing_an_unrelated_engine_does_not_lift_a_pending_violation()
    {
        using (Engine bystander = new(Shanghai.Instance))
        using (Engine offender = new(Shanghai.Instance))
        {
            dynamic hoardingTracer = offender.CreateTracer(HoardingTracer);
            try
            {
                Action hoard = () =>
                {
                    for (int i = 0; i < 400; i++)
                    {
                        hoardingTracer.step(null, null);
                    }
                };
                Assert.That(hoard, Throws.InstanceOf(typeof(IScriptEngineException)), "the hoarding script must trip the limit");
            }
            finally
            {
                ((object)hoardingTracer as IDisposable)?.Dispose();
            }

            bystander.Dispose();

            Assert.That(() => offender.CreateTracer("{ result: function(ctx, db) { return 1; } }"), Throws.InstanceOf(typeof(IScriptEngineException)),
                "the violation must still stand after an unrelated engine was released while the offender is alive");
        }

        using Engine recovered = new(Shanghai.Instance);
        dynamic probe = recovered.CreateTracer("{ result: function(ctx, db) { return 7; } }");

        Assert.That((int)probe.result(), Is.EqualTo(7));
    }

    private const string HoardingTracer = @"{
                    hoard: [],
                    step: function(log, db) { this.hoard.push(new Array(524288).fill(1)); },
                    fault: function(log, db) { },
                    result: function(ctx, db) { return this.hoard.length; }
                }";

    private GethLikeBlockJavaScriptTracer ExecuteTwoTransactionBlock(GethLikeBlockJavaScriptTracer tracer) =>
        ExecuteTwoTransactionBlock(tracer, MStore(), MStore());

    private GethLikeBlockJavaScriptTracer ExecuteTwoTransactionBlock(GethLikeBlockJavaScriptTracer tracer, byte[] firstCode, byte[] secondCode)
    {
        (Block block, Transaction first) = PrepareTx(MainnetSpecProvider.CancunActivation, 100000UL, firstCode);
        tracer.StartNewBlockTrace(block);
        ExecuteTraced(tracer, block, first);
        (_, Transaction second) = PrepareTx(MainnetSpecProvider.CancunActivation, 100000UL, secondCode);
        ExecuteTraced(tracer, block, second);
        tracer.EndBlockTrace();
        return tracer;
    }

    private void ExecuteTraced(IBlockTracer tracer, Block block, Transaction transaction)
    {
        ITxTracer txTracer = tracer.StartNewTxTrace(transaction);
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), txTracer);
        tracer.EndTxTrace();
    }

    private static byte[] PushPopSequence(int count)
    {
        Prepare code = Prepare.EvmCode;
        for (int i = 0; i < count; i++)
        {
            code = code.PushData(1).Op(Instruction.POP);
        }

        return code.Op(Instruction.STOP).Done;
    }

    private const string StepCountingTracer = @"{
                    steps: 0,
                    step: function(log, db) { this.steps++; },
                    fault: function(log, db) { },
                    result: function(ctx, db) { return { steps: this.steps }; }
                }";

    private static readonly JsonSerializerOptions ExpectedResultOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string ResultJson(GethLikeTxTrace trace) => JsonSerializer.Serialize(trace.CustomTracerResult, EthereumJsonSerializer.JsonOptions);

    private static string[] ResultJsons(IReadOnlyCollection<GethLikeTxTrace> traces)
    {
        string[] results = new string[traces.Count];
        int index = 0;
        foreach (GethLikeTxTrace trace in traces)
        {
            results[index++] = ResultJson(trace);
        }

        return results;
    }

    private static void AssertResult(GethLikeTxTrace trace, object expected) =>
        Assert.That(
            ResultJson(trace),
            Is.EqualTo(JsonSerializer.Serialize(expected, ExpectedResultOptions)));

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
