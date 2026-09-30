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
using Nethermind.Specs.Test;
using Nethermind.Evm.State;

namespace Nethermind.Evm.Test.Tracing;

using Nethermind.Blockchain.Tracing.GethStyle.Custom.JavaScript;

public class GethLikeJavaScriptTracerTests : VirtualMachineTestsBase
{
    [TestCase("new Uint8Array([])", "{}")]
    [TestCase("new Uint8Array([0, 1, 255])", "{\"0\":0,\"1\":1,\"2\":255}")]
    [TestCase("new Uint8Array([7, 1, 255, 8]).subarray(1, 3)", "{\"0\":1,\"1\":255}")]
    [TestCase("[]", "[]")]
    [TestCase("[0, 1, 255]", "[0,1,255]")]
    [TestCase("toHex(new Uint8Array([]))", "\"0x\"")]
    [TestCase("toHex(new Uint8Array([0, 1, 255]))", "\"0x0001ff\"")]
    public void Javascript_byte_results_match_json_stringify(string expression, string expected)
    {
        using Engine engine = new(Shanghai.Instance);
        dynamic tracer = engine.CreateTracer("{result:function(){return " + expression + ";}}");
        object result = tracer.result();
        Assert.That(JsonSerializer.Serialize(result, EthereumJsonSerializer.JsonOptions), Is.EqualTo(expected));
    }

    [Test]
    public void Javascript_word_helpers_pad_and_truncate([Values(0, 1, 32, 33, 65)] int length, [Values] bool arrayInput)
    {
        byte[] input = Enumerable.Range(0, length).Select(i => (byte)(i + 1)).ToArray();
        byte[] word = new byte[32];
        int count = Math.Min(length, word.Length);
        input.AsSpan(length - count).CopyTo(word.AsSpan(word.Length - count));
        string hex = "0x" + Convert.ToHexStringLower(input);
        string argument = arrayInput ? JsonSerializer.Serialize(input.Select(b => (int)b).ToArray()) : JsonSerializer.Serialize(hex);
        string address = TestItem.AddressA.ToString();
        using Engine engine = new(Shanghai.Instance);
        dynamic tracer = engine.CreateTracer("{result:function(){return [toHex(toWord(" + argument + ")),toHex(toContract2('" + address + "','" + hex + "',[]))];}}");
        object result = tracer.result();
        string[] expected = ["0x" + Convert.ToHexStringLower(word), ContractAddress.From(TestItem.AddressA, word, []).ToString()];
        Assert.That(JsonSerializer.Serialize(result, EthereumJsonSerializer.JsonOptions), Is.EqualTo(JsonSerializer.Serialize(expected)));
    }

    [TestCase(false, 0)]
    [TestCase(true, 0)]
    [TestCase(true, 1)]
    public void Javascript_db_exists_includes_empty_accounts(bool exists, int balance)
    {
        Address address = new("0x00000000000000000000000000000000deadbeef");
        if (exists) TestState.CreateAccount(address, (UInt256)balance);
        Nethermind.Blockchain.Tracing.GethStyle.Custom.JavaScript.Db db = new(TestState);
        Assert.That(db.exists(address.ToString()), Is.EqualTo(exists));
    }

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

    [TestCase("fe", "INVALID")]
    [TestCase("0c", "opcode 0xc not defined")]
    public void Invalid_opcode_callbacks_have_geth_cost_and_error(string code, string opcode)
    {
        const string userTracer = """
            {
                events: [],
                step: function(log) { this.events.push("step:" + log.op.toString() + ":" + log.getCost() + ":" + (log.getError() || "")); },
                fault: function(log) { this.events.push("fault:" + log.op.toString() + ":" + log.getCost() + ":" + log.getError()); },
                result: function() { return this.events; }
            }
            """;
        using GethLikeBlockJavaScriptTracer tracer = ExecuteBlock(
            GetTracer(userTracer), Bytes.FromHexString(code), MainnetSpecProvider.CancunActivation);
        using GethLikeTxTrace trace = tracer.BuildResult().First();
        AssertResult(trace, new[] { $"step:{opcode}:0:", $"fault:{opcode}:0:invalid opcode: {opcode}" });
    }

    [Test]
    public void Fixed_cost_stack_underflow_is_reported_by_step()
    {
        const string userTracer = """
            {
                events: [],
                step: function(log) { this.events.push("step:" + log.getCost() + ":" + log.getError() + ":" + log.stack.length()); },
                fault: function(log) { this.events.push("fault"); },
                result: function() { return this.events; }
            }
            """;
        using GethLikeBlockJavaScriptTracer tracer = ExecuteBlock(
            GetTracer(userTracer), Prepare.EvmCode.Op(Instruction.ADD).Done, MainnetSpecProvider.CancunActivation);
        using GethLikeTxTrace trace = tracer.BuildResult().First();
        AssertResult(trace, new[] { "step:3:stack underflow (0 <=> 2):0" });
    }

    [TestCase(Instruction.PUSH0, 2)]
    [TestCase(Instruction.DUP1, 3)]
    public void Fixed_cost_stack_overflow_is_reported_by_step(Instruction opcode, int cost)
    {
        const string userTracer = """
            {
                events: [],
                step: function(log) { if (log.getError()) this.events.push("step:" + log.getCost() + ":" + log.getError()); },
                fault: function(log) { this.events.push("fault"); },
                result: function() { return this.events; }
            }
            """;
        byte[] code = new byte[1025];
        Array.Fill(code, (byte)Instruction.PUSH0);
        code[^1] = (byte)opcode;
        using GethLikeBlockJavaScriptTracer tracer = ExecuteBlock(
            GetTracer(userTracer), code, MainnetSpecProvider.CancunActivation);
        using GethLikeTxTrace trace = tracer.BuildResult().First();
        AssertResult(trace, new[] { $"step:{cost}:stack limit reached 1024 (1023)" });
    }

    [TestCase("600156", 100000UL, "step:JUMP:8::1:1:0", "fault:JUMP:8:invalid jump destination:1:1:0")]
    [TestCase("6001600157", 100000UL, "step:JUMPI:10::2:1,1:0", "fault:JUMPI:10:invalid jump destination:2:1,1:0")]
    [TestCase("6000600157", 100000UL, "step:JUMPI:10::2:1,0:0", null)]
    [TestCase("6001600060003e", 100000UL, "step:RETURNDATACOPY:9::3:0,0,1:0", "fault:RETURNDATACOPY:9:return data out of bounds:3:0,1,1:0")]
    [TestCase("6000600060003e", 100000UL, "step:RETURNDATACOPY:3::3:0,0,0:0", null)]
    [TestCase("60006000f3", 100000UL, "step:RETURN:0::2:0,0:0", null)]
    [TestCase("60016000f3", 100000UL, "step:RETURN:3::2:0,1:0", null)]
    [TestCase("60006000fd", 100000UL, "step:REVERT:0::2:0,0:0", "fault:REVERT:0:execution reverted:2:0,0:0")]
    [TestCase("60016000fd", 100000UL, "step:REVERT:3::2:0,1:0", "fault:REVERT:3:execution reverted:2:0,1:0")]
    [TestCase("56", 21000UL, "step:JUMP:8:stack underflow (0 <=> 1):0::0", null)]
    [TestCase("57", 21000UL, "step:JUMPI:10:stack underflow (0 <=> 2):0::0", null)]
    [TestCase("3e", 21000UL, "step:RETURNDATACOPY:3:stack underflow (0 <=> 3):0::0", null)]
    [TestCase("f3", 21000UL, "step:RETURN:0:stack underflow (0 <=> 2):0::0", null)]
    [TestCase("fd", 21000UL, "step:REVERT:0:stack underflow (0 <=> 2):0::0", null)]
    [TestCase("600156", 21003UL, "step:JUMP:8:out of gas:1:1:0", null)]
    [TestCase("6001600157", 21006UL, "step:JUMPI:10:out of gas:2:1,1:0", null)]
    [TestCase("6000600060003e", 21009UL, "step:RETURNDATACOPY:3:out of gas:3:0,0,0:0", null)]
    [TestCase("6001600060003e", 21012UL, "step:RETURNDATACOPY:9:out of gas:3:0,0,1:0", null)]
    [TestCase("60016000f3", 21006UL, "step:RETURN:3:out of gas:2:0,1:0", null)]
    [TestCase("60016000fd", 21006UL, "step:REVERT:3:out of gas:2:0,1:0", null)]
    [TestCase("600160005360406000f3", 100000UL, "step:RETURN:3::2:0,64:32", null)]
    [TestCase("600160005360406000fd", 100000UL, "step:REVERT:3::2:0,64:32", "fault:REVERT:3:execution reverted:2:0,64:32")]
    [TestCase("600160005360006000600160006004612710fa506001600060403e", 100000UL, "step:RETURNDATACOPY:12::3:64,0,1:32", null)]
    [TestCase("51", 21000UL, "step:MLOAD:3:stack underflow (0 <=> 1):0::0", null)]
    [TestCase("52", 21000UL, "step:MSTORE:3:stack underflow (0 <=> 2):0::0", null)]
    [TestCase("53", 21000UL, "step:MSTORE8:3:stack underflow (0 <=> 2):0::0", null)]
    [TestCase("37", 21000UL, "step:CALLDATACOPY:3:stack underflow (0 <=> 3):0::0", null)]
    [TestCase("39", 21000UL, "step:CODECOPY:3:stack underflow (0 <=> 3):0::0", null)]
    [TestCase("600051", 21003UL, "step:MLOAD:3:out of gas:1:0:0", null)]
    [TestCase("600051", 21006UL, "step:MLOAD:6:out of gas:1:0:0", null)]
    [TestCase("6001600052", 21006UL, "step:MSTORE:3:out of gas:2:0,1:0", null)]
    [TestCase("6001600052", 21009UL, "step:MSTORE:6:out of gas:2:0,1:0", null)]
    [TestCase("6001600053", 21006UL, "step:MSTORE8:3:out of gas:2:0,1:0", null)]
    [TestCase("6001600053", 21009UL, "step:MSTORE8:6:out of gas:2:0,1:0", null)]
    [TestCase("60016000600037", 21009UL, "step:CALLDATACOPY:3:out of gas:3:0,0,1:0", null)]
    [TestCase("60016000600037", 21012UL, "step:CALLDATACOPY:9:out of gas:3:0,0,1:0", null)]
    [TestCase("60016000600039", 21009UL, "step:CODECOPY:3:out of gas:3:0,0,1:0", null)]
    [TestCase("60016000600039", 21012UL, "step:CODECOPY:9:out of gas:3:0,0,1:0", null)]
    [TestCase("60006000600037", 100000UL, "step:CALLDATACOPY:3::3:0,0,0:0", null)]
    [TestCase("60006000600039", 100000UL, "step:CODECOPY:3::3:0,0,0:0", null)]
    [TestCase("600051", 100000UL, "step:MLOAD:6::1:0:0", null)]
    [TestCase("6001600052", 100000UL, "step:MSTORE:6::2:0,1:0", null)]
    [TestCase("6001600053", 100000UL, "step:MSTORE8:6::2:0,1:0", null)]
    [TestCase("60016000600037", 100000UL, "step:CALLDATACOPY:9::3:0,0,1:0", null)]
    [TestCase("60016000600039", 100000UL, "step:CODECOPY:9::3:0,0,1:0", null)]
    [TestCase("31", 21000UL, "step:BALANCE:100:stack underflow (0 <=> 1):0::0", null)]
    [TestCase("61beef31", 21003UL, "step:BALANCE:100:out of gas:1:48879:0", null)]
    [TestCase("61beef31", 21103UL, "step:BALANCE:2600:out of gas:1:48879:0", null)]
    [TestCase("61beef31", 100000UL, "step:BALANCE:2600::1:48879:0", null)]
    [TestCase("600431", 100000UL, "step:BALANCE:100::1:4:0", null)]
    [TestCase("3b", 21000UL, "step:EXTCODESIZE:100:stack underflow (0 <=> 1):0::0", null)]
    [TestCase("61beef3b", 21003UL, "step:EXTCODESIZE:100:out of gas:1:48879:0", null)]
    [TestCase("61beef3b", 21103UL, "step:EXTCODESIZE:2600:out of gas:1:48879:0", null)]
    [TestCase("61beef3b", 100000UL, "step:EXTCODESIZE:2600::1:48879:0", null)]
    [TestCase("60043b", 100000UL, "step:EXTCODESIZE:100::1:4:0", null)]
    [TestCase("3f", 21000UL, "step:EXTCODEHASH:100:stack underflow (0 <=> 1):0::0", null)]
    [TestCase("61beef3f", 21003UL, "step:EXTCODEHASH:100:out of gas:1:48879:0", null)]
    [TestCase("61beef3f", 21103UL, "step:EXTCODEHASH:2600:out of gas:1:48879:0", null)]
    [TestCase("61beef3f", 100000UL, "step:EXTCODEHASH:2600::1:48879:0", null)]
    [TestCase("60043f", 100000UL, "step:EXTCODEHASH:100::1:4:0", null)]
    [TestCase("54", 21000UL, "step:SLOAD:0:stack underflow (0 <=> 1):0::0", null)]
    [TestCase("600054", 21003UL, "step:SLOAD:2100:out of gas:1:0:0", null)]
    [TestCase("600054", 100000UL, "step:SLOAD:2100::1:0:0", null)]
    [TestCase("5c", 21000UL, "step:TLOAD:100:stack underflow (0 <=> 1):0::0", null)]
    [TestCase("5d", 21000UL, "step:TSTORE:100:stack underflow (0 <=> 2):0::0", null)]
    [TestCase("60005c", 21003UL, "step:TLOAD:100:out of gas:1:0:0", null)]
    [TestCase("600160005d", 21006UL, "step:TSTORE:100:out of gas:2:0,1:0", null)]
    [TestCase("600160005d", 21106UL, "step:TSTORE:100::2:0,1:0", null)]
    [TestCase("600160005d60005c", 100000UL, "step:TLOAD:100::1:0:0", null)]
    [TestCase("6001600360003e", 100000UL, "step:RETURNDATACOPY:9::3:0,3,1:0", "fault:RETURNDATACOPY:9:return data out of bounds:3:0,4,1:0")]
    [TestCase("60006801000000000000000060003e", 100000UL, "step:RETURNDATACOPY:3::3:0,18446744073709551616,0:0", "fault:RETURNDATACOPY:3:return data out of bounds:3:0,18446744073709551616,0:0")]
    [TestCase("600167ffffffffffffffff60003e", 100000UL, "step:RETURNDATACOPY:9::3:0,18446744073709551615,1:0", "fault:RETURNDATACOPY:9:return data out of bounds:3:0,18446744073709551616,1:0")]
    public void Jump_and_memory_boundaries_preserve_geth_callback_order_and_state(string code, ulong gasLimit, string step, string? fault)
    {
        const string userTracer = """
            {
                events: [],
                record: function(phase, log) {
                    var op = log.op.toString();
                    var stack = [];
                    for (var i = 0; i < log.stack.length(); i++) stack.push(log.stack.peek(i).toString());
                    if (log.op.toNumber() === 0x$opcode)
                        this.events.push(phase + ":" + op + ":" + log.getCost() + ":" + (log.getError() || "") + ":" +
                            log.stack.length() + ":" + stack.join(",") + ":" + log.memory.length());
                },
                step: function(log) { this.record("step", log); },
                fault: function(log) { this.record("fault", log); },
                result: function() { return this.events; }
            }
            """;
        using GethLikeBlockJavaScriptTracer tracer = GetTracer(userTracer.Replace("$opcode", code[^2..], StringComparison.Ordinal));
        (Block block, Transaction transaction) = PrepareTx(MainnetSpecProvider.CancunActivation, gasLimit, Bytes.FromHexString(code));
        tracer.StartNewBlockTrace(block);
        ExecuteTraced(tracer, block, transaction);
        tracer.EndBlockTrace();
        using GethLikeTxTrace trace = tracer.BuildResult().First();
        AssertResult(trace, fault is null ? new[] { step } : new[] { step, fault });
    }

    [TestCase("600051", 3, "0", "aa", false)]
    [TestCase("60bb600052", 3, "0,187", "bb", true)]
    [TestCase("60bb600053", 3, "0,187", "bb", false)]
    [TestCase("60016000600037", 6, "0,0,1", "00", false)]
    [TestCase("60016000600039", 6, "0,0,1", "60", false)]
    public void Memory_callbacks_observe_pre_effect_stack_memory_and_database(string operation, int cost, string operands, string finalByte, bool rightAligned)
    {
        const string prefix = "600160005560aa600053";
        const string suffix = "600260005500";
        string userTracer = $$"""
            {
                events: [],
                step: function(log, db) {
                    if (log.getPC() !== {{(prefix.Length + operation.Length) / 2 - 1}} && log.op.toNumber() !== 0) return;
                    var stack = [];
                    for (var i = 0; i < log.stack.length(); i++) stack.push(log.stack.peek(i).toString());
                    var memory = toHex(log.memory.slice(0, 32));
                    var storage = toHex(db.getState(log.contract.getAddress(), toWord("0x00")));
                    this.events.push(log.op.toNumber() === 0 ? "end:" + memory + ":" + storage :
                        "step:" + log.getCost() + ":" + stack.join(",") + ":" + log.memory.length() + ":" + memory + ":" + storage);
                },
                fault: function(log) { this.events.push("fault:" + log.getError()); },
                result: function() { return this.events; }
            }
            """;
        using GethLikeBlockJavaScriptTracer tracer = ExecuteBlock(
            GetTracer(userTracer), Bytes.FromHexString(prefix + operation + suffix), MainnetSpecProvider.CancunActivation);
        using GethLikeTxTrace trace = tracer.BuildResult().First();
        string initialStorage = "1".PadLeft(64, '0');
        string finalStorage = "2".PadLeft(64, '0');
        string initialMemory = "aa".PadRight(64, '0');
        string finalMemory = rightAligned ? finalByte.PadLeft(64, '0') : finalByte.PadRight(64, '0');
        AssertResult(trace, new[]
        {
            $"step:{cost}:{operands}:32:0x{initialMemory}:0x{initialStorage}",
            $"end:0x{finalMemory}:0x{finalStorage}"
        });
    }

    [TestCase("31", MainnetSpecProvider.ConstantinopleFixBlockNumber, 400, 400)]
    [TestCase("3b", MainnetSpecProvider.ConstantinopleFixBlockNumber, 700, 700)]
    [TestCase("3f", MainnetSpecProvider.ConstantinopleFixBlockNumber, 400, 400)]
    [TestCase("54", MainnetSpecProvider.ConstantinopleFixBlockNumber, 200, 200)]
    [TestCase("31", MainnetSpecProvider.IstanbulBlockNumber, 700, 700)]
    [TestCase("3b", MainnetSpecProvider.IstanbulBlockNumber, 700, 700)]
    [TestCase("3f", MainnetSpecProvider.IstanbulBlockNumber, 700, 700)]
    [TestCase("54", MainnetSpecProvider.IstanbulBlockNumber, 800, 800)]
    [TestCase("31", MainnetSpecProvider.BerlinBlockNumber, 2600, 100)]
    [TestCase("3b", MainnetSpecProvider.BerlinBlockNumber, 2600, 100)]
    [TestCase("3f", MainnetSpecProvider.BerlinBlockNumber, 2600, 100)]
    [TestCase("54", MainnetSpecProvider.BerlinBlockNumber, 2100, 100)]
    public void Account_and_storage_callbacks_use_active_fork_costs_before_overwriting_operands(string opcode, ulong blockNumber, int firstCost, int secondCost)
    {
        string userTracer = $$"""
            {
                events: [],
                step: function(log, db) {
                    if (log.op.toNumber() !== 0x{{opcode}}) return;
                    var storage = parseInt(toHex(db.getState(log.contract.getAddress(), toWord("0x00"))), 16);
                    this.events.push(log.getCost() + ":" + log.stack.length() + ":" + log.stack.peek(0).toString() + ":" + storage + ":" + (log.getError() || ""));
                },
                fault: function(log) { this.events.push("fault:" + log.getError()); },
                result: function() { return this.events; }
            }
            """;
        string push = opcode == "54" ? "6000" : "61beef";
        byte[] code = Bytes.FromHexString(push + opcode + "50" + push + opcode);
        TestState.CreateAccount(Recipient, 1.Ether);
        TestState.Set(new StorageCell(Recipient, 0), (UInt256)42);
        Address external = new("0x000000000000000000000000000000000000beef");
        TestState.CreateAccount(external, 7);
        TestState.InsertCode(external, Bytes.FromHexString("6000"), Spec);
        (Block block, Transaction transaction) = PrepareTx((blockNumber, 0), 100000UL, code);
        using GethLikeBlockJavaScriptTracer tracer = new(TestState, SpecProvider.GetSpec(block.Header), GethTraceOptions.Default with { EnableMemory = true, Tracer = userTracer });
        tracer.StartNewBlockTrace(block);
        ExecuteTraced(tracer, block, transaction);
        tracer.EndBlockTrace();
        using GethLikeTxTrace trace = tracer.BuildResult().First();
        string operand = opcode == "54" ? "0" : "48879";
        AssertResult(trace, new[] { $"{firstCost}:1:{operand}:42:", $"{secondCost}:1:{operand}:42:" });
    }

    [TestCase("5d", "step:100:stack underflow (0 <=> 2)", null)]
    [TestCase("600160005d", "step:100:", "fault:100:write protection")]
    public void Static_transient_store_preserves_precondition_and_execution_failure_callbacks(string childCode, string step, string? fault)
    {
        const string userTracer = """
            {
                events: [],
                step: function(log) { if (log.op.toNumber() === 0x5d) this.events.push("step:" + log.getCost() + ":" + (log.getError() || "")); },
                fault: function(log) { if (log.op.toNumber() === 0x5d) this.events.push("fault:" + log.getCost() + ":" + log.getError()); },
                result: function() { return this.events; }
            }
            """;
        TestState.CreateAccount(TestItem.AddressC, 1.Ether);
        TestState.InsertCode(TestItem.AddressC, Bytes.FromHexString(childCode), Spec);
        byte[] code = Prepare.EvmCode.PushData(0).PushData(0).PushData(0).PushData(0)
            .PushData(TestItem.AddressC).PushData(50000).Op(Instruction.STATICCALL).Op(Instruction.STOP).Done;
        using GethLikeBlockJavaScriptTracer tracer = ExecuteBlock(GetTracer(userTracer), code, MainnetSpecProvider.CancunActivation);
        using GethLikeTxTrace trace = tracer.BuildResult().First();
        AssertResult(trace, fault is null ? new[] { step } : new[] { step, fault });
    }

    [TestCase(false, "00", true)]
    [TestCase(false, "60006000fd", false)]
    [TestCase(false, "fe", false)]
    [TestCase(true, "00", true)]
    [TestCase(true, "60006000fd", false)]
    [TestCase(true, "fe", false)]
    public void Storage_refund_counter_tracks_child_commit_and_rollback(bool clearParentStorage, string childEnding, bool childCommits)
    {
        const string userTracer = """
            {
                events: [],
                child: null,
                step: function(log, db) {
                    if (log.getDepth() === 2 && log.getPC() === 5) {
                        this.child = log.contract.getAddress();
                        this.events.push("child:" + log.getRefund());
                    }
                    if (log.getDepth() === 1 && log.op.toNumber() === 0) {
                        var childStorage = parseInt(toHex(db.getState(this.child, toWord("0x00"))), 16);
                        this.events.push("parent:" + log.getRefund() + ":" + childStorage);
                    }
                },
                fault: function(log) { },
                result: function() { return this.events; }
            }
            """;
        TestState.CreateAccount(Recipient, 1.Ether);
        TestState.Set(new StorageCell(Recipient, 0), UInt256.One);
        TestState.CreateAccount(TestItem.AddressC, 1.Ether);
        TestState.Set(new StorageCell(TestItem.AddressC, 0), UInt256.One);
        TestState.InsertCode(TestItem.AddressC, Bytes.FromHexString("6000600055" + childEnding), Spec);
        Prepare code = Prepare.EvmCode;
        if (clearParentStorage)
            code = code.PushData(0).PushData(0).Op(Instruction.SSTORE);
        byte[] bytecode = code.Call(TestItem.AddressC, 50000).Op(Instruction.POP).Op(Instruction.STOP).Done;
        using GethLikeBlockJavaScriptTracer tracer = ExecuteBlock(GetTracer(userTracer), bytecode, MainnetSpecProvider.CancunActivation);
        using GethLikeTxTrace trace = tracer.BuildResult().First();
        int parentRefund = clearParentStorage ? 4800 : 0;
        AssertResult(trace, new[]
        {
            $"child:{parentRefund + 4800}",
            $"parent:{parentRefund + (childCommits ? 4800 : 0)}:{(childCommits ? 0 : 1)}"
        });
    }

    [TestCase(MainnetSpecProvider.IstanbulBlockNumber, 24000)]
    [TestCase(MainnetSpecProvider.BerlinBlockNumber, 24000)]
    [TestCase(MainnetSpecProvider.LondonBlockNumber, 0)]
    public void Selfdestruct_finalization_does_not_double_count_refund_in_retained_log(ulong blockNumber, int expectedRefund)
    {
        const string userTracer = """
            {
                last: null,
                step: function(log) { this.last = log; },
                fault: function(log) { },
                result: function() { return this.last.getRefund(); }
            }
            """;
        TestState.CreateAccount(TestItem.AddressC, 1.Ether);
        byte[] code = Prepare.EvmCode.PushData(TestItem.AddressC).Op(Instruction.SELFDESTRUCT).Done;
        (Block block, Transaction transaction) = PrepareTx((blockNumber, 0), 100000UL, code);
        using GethLikeBlockJavaScriptTracer tracer = new(TestState, SpecProvider.GetSpec(block.Header), GethTraceOptions.Default with { Tracer = userTracer });
        tracer.StartNewBlockTrace(block);
        ExecuteTraced(tracer, block, transaction);
        tracer.EndBlockTrace();
        using GethLikeTxTrace trace = tracer.BuildResult().First();
        AssertResult(trace, expectedRefund);
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
        string[] expectedStrings = { "0:PUSH32:3:79000:0", "33:PUSH1:3:78997:0", "35:MSTORE:6:78994:0", "36:PUSH32:3:78988:0", "69:PUSH1:3:78985:0", "71:MSTORE:6:78982:0", "72:STOP:0:78976:0" };
        AssertResult(traces, expectedStrings);
    }

    [TestCase("../JSTracers/callTracer_legacy")]
    [TestCase(null)]
    [TestCase("{ ) }")]
    public void Unusable_tracer_is_refused_on_construction(string? tracer) =>
        Assert.That(() => GetTracer(tracer!).Dispose(), Throws.ArgumentException);

    [TestCase("flatCallTracer")]
    [TestCase("noSuchTracer.js")]
    [TestCase("_bigInteger")]
    [TestCase("callTracer_legacy.tracer")]
    [TestCase("callTracer_legacy")]
    [TestCase(" opcountTracer.js ")]
    [TestCase("{ result: function(ctx, db) { return null } }")]
    public void Usable_tracer_is_accepted_on_construction(string tracer) =>
        Assert.That(() => GetTracer(tracer).Dispose(), Throws.Nothing);

    [TestCase("CreateUint8ArrayCode")]
    [TestCase("_bigInteger")]
    public void Unknown_expression_does_not_shadow_runtime_helpers(string selector)
    {
        using GethLikeBlockJavaScriptTracer tracer = GetTracer(selector);
        Assert.That(() => ExecuteBlock(tracer, MStore(), MainnetSpecProvider.CancunActivation),
            Throws.TypeOf<System.IO.InvalidDataException>().With.Message.StartWith($"ReferenceError: {selector} is not defined"));
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
        AssertResult(traces, "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358:0xb7705ae4c6f81b66cdb323c65f4e8133690fc099:0x");
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
        string caller = "1:0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358";
        string callee = "2:0x76e68a8696537e4141926f3e528733af9e237d69";
        AssertResult(traces, new[] { caller, callee, caller });
    }

    [Test]
    public void post_step_fires_once_per_step()
    {
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
        Assert.That(int.Parse(counts[1]), Is.EqualTo(steps), "postStep must fire once per step");
    }

    [TestCase("5f5f20", "S0:PUSH0,P0:PUSH0,S1:PUSH0,P1:PUSH0,S2:KECCAK256,P2:KECCAK256,S3:STOP,P3:STOP", 0, TestName = "Callbacks_ordered_fallthrough_to_implicit_stop")]
    // A PUSH truncated by the end of code moves the counter past the code length; the implicit STOP is traced at that counter.
    [TestCase("5f61ff", "S0:PUSH0,P0:PUSH0,S1:PUSH2,P1:PUSH2,S4:STOP,P4:STOP", 0, TestName = "Callbacks_ordered_truncated_push_to_implicit_stop")]
    [TestCase("5f5f205000", "S0:PUSH0,P0:PUSH0,S1:PUSH0,P1:PUSH0,S2:KECCAK256,P2:KECCAK256,S3:POP,P3:POP,S4:STOP,P4:STOP", 0, TestName = "Callbacks_ordered_mid_code_opcode")]
    [TestCase("00", "S0:STOP,P0:STOP", 0, TestName = "Callbacks_ordered_explicit_stop")]
    [TestCase("5f5ff3", "S0:PUSH0,P0:PUSH0,S1:PUSH0,P1:PUSH0,S2:RETURN,P2:RETURN", 0, TestName = "Callbacks_ordered_explicit_return")]
    // REVERT executes after step; Geth's deferred fault follows execution (and Nethermind's postStep extension).
    [TestCase("5f5ffd", "S0:PUSH0,P0:PUSH0,S1:PUSH0,P1:PUSH0,S2:REVERT,P2:REVERT,F2:REVERT", 1, TestName = "Callbacks_ordered_explicit_revert")]
    [TestCase("5fff", "S0:PUSH0,P0:PUSH0,S1:SELFDESTRUCT,P1:SELFDESTRUCT", 0, TestName = "Callbacks_ordered_explicit_self_destruct")]
    // Geth reports pre-execution failures through step with an error, without a second fault callback.
    [TestCase("20", "S0:KECCAK256,P0:KECCAK256", 0, TestName = "Callbacks_ordered_stack_underflow")]
    [TestCase("63ffffffff5f20", "S0:PUSH4,P0:PUSH4,S5:PUSH0,P5:PUSH0,S6:KECCAK256,P6:KECCAK256", 0, TestName = "Callbacks_ordered_out_of_gas")]
    [TestCase("5f5f57", "S0:PUSH0,P0:PUSH0,S1:PUSH0,P1:PUSH0,S2:JUMPI,P2:JUMPI,S3:STOP,P3:STOP", 0, TestName = "Callbacks_ordered_jumpi_falls_off_code")]
    [TestCase("fe", "S0:INVALID,P0:INVALID,F0:INVALID", 1, TestName = "Callbacks_ordered_invalid_opcode")]
    [TestCase("0f", "S0:opcode 0xf not defined,P0:opcode 0xf not defined,F0:opcode 0xf not defined", 1, TestName = "Callbacks_ordered_undefined_opcode")]
    public void Step_post_step_and_fault_callbacks_are_ordered(string codeHex, string sequence, int faults)
    {
        string userTracer = @"{
                    sequence: [],
                    faults: 0,
                    step: function(log, db) { this.sequence.push('S' + log.getPC() + ':' + log.op.toString()) },
                    postStep: function(log, db) { this.sequence.push('P' + log.getPC() + ':' + log.op.toString()) },
                    fault: function(log, db) { this.sequence.push('F' + log.getPC() + ':' + log.op.toString()); this.faults++ },
                    result: function(ctx, db) { return this.sequence.join(',') + '|' + this.faults }
                }";

        using GethLikeBlockJavaScriptTracer tracer = ExecuteBlock(
            GetTracer(userTracer),
            Bytes.FromHexString(codeHex),
            MainnetSpecProvider.CancunActivation);
        using GethLikeTxTrace traces = tracer.BuildResult().First();

        AssertResult(traces, $"{sequence}|{faults}");
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
        string[] expectedStrings = { "0x" + SampleHexData1.PadLeft(64, '0'), "0x" + SampleHexData2.PadLeft(64, '0') };
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

        Assert.That(JsonSerializer.Serialize(traces.CustomTracerResult?.Value), Is.EqualTo("{\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\":{\"balance\":\"0x56bc75e2d63100000\",\"nonce\":0,\"code\":\"0x60006000600060007376e68a8696537e4141926f3e528733af9e237d6961c350f400\",\"storage\":{}},\"0x76e68a8696537e4141926f3e528733af9e237d69\":{\"balance\":\"0xde0b6b3a7640000\",\"nonce\":0,\"code\":\"0x7f7f000000000000000000000000000000000000000000000000000000000000006000527f0060005260036000f30000000000000000000000000000000000000000000000602052602960006000f000\",\"storage\":{}},\"0x89aa9b2ce05aaef815f25b237238c0b4ffff6ae3\":{\"balance\":\"0x0\",\"nonce\":0,\"code\":\"0x\",\"storage\":{}},\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\":{\"balance\":\"0x56bc75e2d63100000\",\"nonce\":0,\"code\":\"0x\",\"storage\":{}}}"));
    }

    [Test]
    public void call_tracer()
    {
        using GethLikeBlockJavaScriptTracer tracer = ExecuteBlock(
                GetTracer("callTracer_legacy"),
                NestedCalls(),
                MainnetSpecProvider.CancunActivation);
        using GethLikeTxTrace traces = tracer.BuildResult().First();

        Assert.That(JsonSerializer.Serialize(traces.CustomTracerResult?.Value), Is.EqualTo("{\"type\":\"CALL\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"to\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"value\":\"0x1\",\"gas\":\"0x186a0\",\"gasUsed\":\"0xdbd1\",\"input\":\"0x\",\"output\":\"0x\",\"calls\":[{\"type\":\"DELEGATECALL\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"to\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"gas\":\"0xc350\",\"gasUsed\":\"0x7f8f\",\"input\":\"0x\",\"output\":\"0x\",\"calls\":[{\"type\":\"CREATE\",\"from\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"to\":\"0x89aa9b2ce05aaef815f25b237238c0b4ffff6ae3\",\"value\":\"0x0\",\"gas\":\"0x4513\",\"gasUsed\":\"0x26a\",\"input\":\"0x7f000000000000000000000000000000000000000000000000000000000000000060005260036000f3\",\"output\":\"0x000000\"}]}]}"));
    }

    [Test]
    public void _4byte_tracer_legacy()
    {
        using GethLikeBlockJavaScriptTracer tracer = ExecuteBlock(
                GetTracer("4byteTracer_legacy"),
                CallWithInput(),
                MainnetSpecProvider.CancunActivation);
        using GethLikeTxTrace traces = tracer.BuildResult().First();

        Assert.That(JsonSerializer.Serialize(traces.CustomTracerResult?.Value), Is.EqualTo("{\"0x00000000-1\":2,\"0x00000000-2\":1}"));
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
                bytes = "0x0000000000000000000000000000000000000000000000000000000000000001"
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

        AssertResult(trace, "0x0000000000000000000000000000000000000000000000000000000000000001");
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
    public void Tracer_reused_across_block_traces_traces_every_block()
    {
        const string stepAndIndexTracer = @"{
                    steps: 0,
                    step: function(log, db) { this.steps++; },
                    fault: function(log, db) { },
                    result: function(ctx, db) { return { steps: this.steps, txIndex: ctx.txIndex }; }
                }";
        using GethLikeBlockJavaScriptTracer tracer = GetTracer(stepAndIndexTracer);

        for (int block = 0; block < 2; block++)
        {
            ExecuteTwoTransactionBlock(tracer);

            Assert.That(ResultJsons(tracer.BuildResult()),
                Is.EqualTo(new[] { """{"steps":7,"txIndex":0}""", """{"steps":7,"txIndex":1}""" }), $"block {block}");
        }
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
    public void Engine_in_another_runtime_stays_usable_while_a_violation_is_pending()
    {
        using Engine hoarder = new(Shanghai.Instance);
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

            using Engine bystander = new(Shanghai.Instance);
            dynamic probe = bystander.CreateTracer("{ result: function(ctx, db) { return 7; } }");

            Assert.That((int)probe.result(), Is.EqualTo(7), "an engine in another runtime is unaffected");
            Assert.That(() => hoarder.CreateTracer("{ result: function(ctx, db) { return 1; } }"), Throws.InstanceOf(typeof(IScriptEngineException)),
                "the violation stands in the hoarder's own runtime while it is alive");
        }
        finally
        {
            ((object)hoardingTracer as IDisposable)?.Dispose();
        }
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
                                             fault: function(log, db) { this.step(log, db); }
                                         }
                                         """;
}


public class GethLikeJavaScriptSStoreTracerTests : VirtualMachineTestsBase
{
    protected override ISpecProvider SpecProvider => new CustomSpecProvider(
        ((ForkActivation)0, Frontier.Instance), ((ForkActivation)1, Constantinople.Instance),
        ((ForkActivation)2, ConstantinopleFix.Instance), ((ForkActivation)3, Istanbul.Instance),
        ((ForkActivation)4, Berlin.Instance), ((ForkActivation)5, London.Instance),
        ((ForkActivation)6, Shanghai.Instance), ((ForkActivation)7, Amsterdam.Instance));

    [TestCaseSource(nameof(StorageCases))]
    public void SStore_callbacks_capture_cost_refund_and_operands_before_mutation(
        ulong blockNumber, string code, int initial, int pc, ulong gas, string expected, int final)
    {
        TestState.CreateAccount(Recipient, 1.Ether);
        StorageCell cell = new(Recipient, 0);
        TestState.Set(cell, (UInt256)(uint)initial);
        (Block block, Transaction transaction) = PrepareTx((blockNumber, 0), gas, Bytes.FromHexString(code), blockGasLimit: 40000000);
        if (blockNumber == 7 && gas < 100000)
            transaction.GasLimit = IntrinsicGasCalculator.Calculate(transaction, Amsterdam.Instance, block.Header.GasLimit).Standard + 6 + gas;
        using GethLikeBlockJavaScriptTracer tracer = new(TestState, SpecProvider.GetSpec(block.Header),
            GethTraceOptions.Default with { EnableMemory = true, Tracer = StorageTracer(pc) });
        tracer.StartNewBlockTrace(block);
        ITxTracer txTracer = ((IBlockTracer)tracer).StartNewTxTrace(transaction);
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), txTracer);
        tracer.EndTxTrace();
        tracer.EndBlockTrace();
        using GethLikeTxTrace trace = tracer.BuildResult().First();
        TestState.Get(cell, out UInt256 finalValue);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(JsonSerializer.Serialize(trace.CustomTracerResult, EthereumJsonSerializer.JsonOptions),
                Is.EqualTo(JsonSerializer.Serialize(new[] { expected }, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping })));
            Assert.That(finalValue, Is.EqualTo((UInt256)(uint)final));
        }
    }

    [Test]
    public void Static_SStore_checks_stack_before_write_protection_and_sentry(
        [Values(2UL, 3UL, 4UL, 5UL, 6UL)] ulong blockNumber,
        [Values("55", "600055", "6001600055")] string childCode,
        [Values(2306, 50000)] int childGas)
    {
        TestState.CreateAccount(TestItem.AddressC, 1.Ether);
        TestState.InsertCode(TestItem.AddressC, Bytes.FromHexString(childCode), SpecProvider.GetSpec((ForkActivation)blockNumber));
        TestState.Set(new StorageCell(TestItem.AddressC, 0), UInt256.One);
        byte[] code = Prepare.EvmCode.PushData(0).PushData(0).PushData(0).PushData(0)
            .PushData(TestItem.AddressC).PushData(childGas).Op(Instruction.STATICCALL).Op(Instruction.STOP).Done;
        int depth = (childCode.Length - 2) / 4;
        using GethLikeBlockJavaScriptTracer tracer = new(TestState, SpecProvider.GetSpec((ForkActivation)blockNumber),
            GethTraceOptions.Default with { EnableMemory = true, Tracer = StorageTracer((childCode.Length - 2) / 2) });
        ExecuteBlock(tracer, code, (blockNumber, 0));
        using GethLikeTxTrace trace = tracer.BuildResult().First();
        string error = depth == 2 ? "out of gas: write protection" : $"stack underflow ({depth} <=> 2)";
        string operands = depth == 2 ? "0,1" : depth == 1 ? "0" : "";
        string expected = $"step:0:0:{error}:{operands}:1:0";
        Assert.That(JsonSerializer.Serialize(trace.CustomTracerResult, EthereumJsonSerializer.JsonOptions),
            Is.EqualTo(JsonSerializer.Serialize(new[] { expected }, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping })));
    }

    [TestCase(0UL)]
    [TestCase(2UL)]
    public void Legacy_SStore_initial_oog_native_trace_is_unchanged_when_composed(ulong blockNumber)
    {
        TestState.CreateAccount(Recipient, 1.Ether);
        StorageCell cell = new(Recipient, 0);
        TestState.Set(cell, UInt256.One);
        string? nativeEntries = null;
        foreach (bool composite in new[] { false, true })
        {
            (Block block, Transaction transaction) = PrepareTx((blockNumber, 0), 21007UL, Bytes.FromHexString("600060005500"));
            using GethLikeTxMemoryTracer native = new(transaction, GethTraceOptions.Default);
            using GethLikeBlockJavaScriptTracer script = new(TestState, SpecProvider.GetSpec(block.Header),
                GethTraceOptions.Default with { Tracer = StorageTracer(4) });
            script.StartNewBlockTrace(block);
            ITxTracer javascript = ((IBlockTracer)script).StartNewTxTrace(transaction);
            _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)),
                composite ? new CompositeTxTracer(native, javascript) : native);
            script.EndTxTrace();
            script.EndBlockTrace();
            using GethLikeTxTrace nativeResult = native.BuildResult();
            string entries = JsonSerializer.Serialize(nativeResult.Entries, EthereumJsonSerializer.JsonOptions);
            if (!composite)
                nativeEntries = entries;
            else
            {
                using GethLikeTxTrace scriptResult = script.BuildResult().First();
                TestState.Get(cell, out UInt256 finalValue);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(entries, Is.EqualTo(nativeEntries));
                    Assert.That(finalValue, Is.EqualTo(UInt256.One));
                    Assert.That(JsonSerializer.Serialize(scriptResult.CustomTracerResult, EthereumJsonSerializer.JsonOptions),
                        Is.EqualTo("[\"step:5000:15000:out of gas:0,0:1:0\"]"));
                }
            }
        }
    }

    private static string StorageTracer(int pc) => $$"""
        {
            events: [],
            capture: function(phase, log, db) {
                if (log.getPC() !== {{pc}} || log.op.toNumber() !== 0x55) return;
                var stack = [];
                for (var i = 0; i < log.stack.length(); i++) stack.push(log.stack.peek(i).toString());
                var storage = parseInt(toHex(db.getState(log.contract.getAddress(), toWord("0x00"))), 16);
                this.events.push(phase + ":" + log.getCost() + ":" + log.getRefund() + ":" + (log.getError() || "") + ":" + stack.join(",") + ":" + storage + ":" + log.memory.length());
            },
            step: function(log, db) { this.capture("step", log, db); },
            fault: function(log, db) { this.capture("fault", log, db); },
            result: function() { return this.events; }
        }
        """;

    private static IEnumerable<TestCaseData> StorageCases()
    {
        foreach (ulong gas in new[] { 250000UL, 20000000UL })
        {
            yield return new TestCaseData(7UL, "600060005500", 0, 4, gas, "step:2100:0::0,0:0:0", 0).SetName($"SStore_Amsterdam_noop_{gas}");
            yield return new TestCaseData(7UL, "600160005500", 0, 4, gas, "step:12100:0::0,1:0:0", 1).SetName($"SStore_Amsterdam_create_{gas}");
            yield return new TestCaseData(7UL, "600060005500", 1, 4, gas, "step:12100:11616::0,0:1:0", 0).SetName($"SStore_Amsterdam_clear_{gas}");
            yield return new TestCaseData(7UL, "6001600055600060005500", 0, 9, gas, "step:100:10000::0,0:1:0", 0).SetName($"SStore_Amsterdam_reverse_zero_{gas}");
            yield return new TestCaseData(7UL, "6000600055600160005500", 1, 9, gas, "step:100:10000::0,1:0:0", 1).SetName($"SStore_Amsterdam_reverse_one_{gas}");
            yield return new TestCaseData(7UL, "6000600055600260005500", 1, 9, gas, "step:100:0::0,2:0:0", 2).SetName($"SStore_Amsterdam_replace_cleared_{gas}");
        }
        yield return new TestCaseData(7UL, "600160005500", 0, 4, 50000UL, "step:12100:0:out of gas:0,1:0:0", 0).SetName("SStore_Amsterdam_state_spill_oog");
        yield return new TestCaseData(7UL, "600060005500", 1, 4, 10000UL, "step:12100:11616:out of gas:0,0:1:0", 1).SetName("SStore_Amsterdam_clear_execution_oog");
        yield return new TestCaseData(7UL, "600060005500", 0, 4, 2300UL, "step:0:0:out of gas: not enough gas for reentrancy sentry:0,0:0:0", 0).SetName("SStore_Amsterdam_sentry");
        yield return new TestCaseData(7UL, "600060005500", 0, 4, 2301UL, "step:2100:0::0,0:0:0", 0).SetName("SStore_Amsterdam_above_sentry_noop");
        for (ulong fork = 0; fork <= 6; fork++)
        {
            yield return new TestCaseData(fork, "55", 1, 0, 21000UL, "step:0:0:stack underflow (0 <=> 2)::1:0", 1).SetName($"SStore_stack_underflow_empty_fork_{fork}");
            yield return new TestCaseData(fork, "600055", 1, 2, 21003UL, "step:0:0:stack underflow (1 <=> 2):0:1:0", 1).SetName($"SStore_stack_underflow_one_fork_{fork}");
        }
        yield return new TestCaseData(0UL, "600060005500", 0, 4, 51006UL, "step:5000:0::0,0:0:0", 0).SetName("SStore_Frontier_noop_zero");
        yield return new TestCaseData(0UL, "600160005500", 1, 4, 51006UL, "step:5000:0::0,1:1:0", 1).SetName("SStore_Frontier_noop_one");
        yield return new TestCaseData(0UL, "600160005500", 0, 4, 51006UL, "step:20000:0::0,1:0:0", 1).SetName("SStore_Frontier_set");
        yield return new TestCaseData(0UL, "600260005500", 1, 4, 51006UL, "step:5000:0::0,2:1:0", 2).SetName("SStore_Frontier_reset");
        yield return new TestCaseData(0UL, "600060005500", 1, 4, 51006UL, "step:5000:15000::0,0:1:0", 0).SetName("SStore_Frontier_clear");
        yield return new TestCaseData(0UL, "600160005500", 0, 4, 26005UL, "step:20000:0:out of gas:0,1:0:0", 0).SetName("SStore_Frontier_set_initial_charge_oog");
        yield return new TestCaseData(0UL, "600060005500", 1, 4, 26005UL, "step:5000:15000:out of gas:0,0:1:0", 1).SetName("SStore_Frontier_clear_initial_charge_oog");
        yield return new TestCaseData(0UL, "600160005500", 0, 4, 41005UL, "step:20000:0:out of gas:0,1:0:0", 0).SetName("SStore_Frontier_set_extra_charge_oog");
        yield return new TestCaseData(0UL, "600160005500", 0, 4, 41006UL, "step:20000:0::0,1:0:0", 1).SetName("SStore_Frontier_set_exact");
        yield return new TestCaseData(0UL, "600060005500", 1, 4, 26006UL, "step:5000:15000::0,0:1:0", 0).SetName("SStore_Frontier_clear_exact");
        yield return new TestCaseData(0UL, "6001600055600060005500", 0, 9, 71012UL, "step:5000:15000::0,0:1:0", 0).SetName("SStore_Frontier_set_then_clear");
        yield return new TestCaseData(0UL, "6000600055600160005500", 1, 9, 56012UL, "step:20000:15000::0,1:0:0", 1).SetName("SStore_Frontier_clear_restore");
        yield return new TestCaseData(0UL, "60006000556001600055600060005500", 1, 14, 76018UL, "step:5000:30000::0,0:1:0", 0).SetName("SStore_Frontier_clear_restore_clear");
        yield return new TestCaseData(2UL, "600060005500", 0, 4, 51006UL, "step:5000:0::0,0:0:0", 0).SetName("SStore_ConstantinopleFix_noop_zero");
        yield return new TestCaseData(2UL, "600160005500", 1, 4, 51006UL, "step:5000:0::0,1:1:0", 1).SetName("SStore_ConstantinopleFix_noop_one");
        yield return new TestCaseData(2UL, "600160005500", 0, 4, 51006UL, "step:20000:0::0,1:0:0", 1).SetName("SStore_ConstantinopleFix_set");
        yield return new TestCaseData(2UL, "600260005500", 1, 4, 51006UL, "step:5000:0::0,2:1:0", 2).SetName("SStore_ConstantinopleFix_reset");
        yield return new TestCaseData(2UL, "600060005500", 1, 4, 51006UL, "step:5000:15000::0,0:1:0", 0).SetName("SStore_ConstantinopleFix_clear");
        yield return new TestCaseData(2UL, "600160005500", 0, 4, 26005UL, "step:20000:0:out of gas:0,1:0:0", 0).SetName("SStore_ConstantinopleFix_set_initial_charge_oog");
        yield return new TestCaseData(2UL, "600060005500", 1, 4, 26005UL, "step:5000:15000:out of gas:0,0:1:0", 1).SetName("SStore_ConstantinopleFix_clear_initial_charge_oog");
        yield return new TestCaseData(2UL, "600160005500", 0, 4, 41005UL, "step:20000:0:out of gas:0,1:0:0", 0).SetName("SStore_ConstantinopleFix_set_extra_charge_oog");
        yield return new TestCaseData(2UL, "600160005500", 0, 4, 41006UL, "step:20000:0::0,1:0:0", 1).SetName("SStore_ConstantinopleFix_set_exact");
        yield return new TestCaseData(2UL, "600060005500", 1, 4, 26006UL, "step:5000:15000::0,0:1:0", 0).SetName("SStore_ConstantinopleFix_clear_exact");
        yield return new TestCaseData(2UL, "6001600055600060005500", 0, 9, 71012UL, "step:5000:15000::0,0:1:0", 0).SetName("SStore_ConstantinopleFix_set_then_clear");
        yield return new TestCaseData(2UL, "6000600055600160005500", 1, 9, 56012UL, "step:20000:15000::0,1:0:0", 1).SetName("SStore_ConstantinopleFix_clear_restore");
        yield return new TestCaseData(2UL, "60006000556001600055600060005500", 1, 14, 76018UL, "step:5000:30000::0,0:1:0", 0).SetName("SStore_ConstantinopleFix_clear_restore_clear");
        yield return new TestCaseData(1UL, "600160005500", 1, 4, 51006UL, "step:200:0::0,1:1:0", 1).SetName("SStore_Constantinople_noop");
        yield return new TestCaseData(1UL, "600160005500", 0, 4, 51006UL, "step:20000:0::0,1:0:0", 1).SetName("SStore_Constantinople_set");
        yield return new TestCaseData(1UL, "600260005500", 1, 4, 51006UL, "step:5000:0::0,2:1:0", 2).SetName("SStore_Constantinople_reset");
        yield return new TestCaseData(1UL, "600060005500", 1, 4, 51006UL, "step:5000:15000::0,0:1:0", 0).SetName("SStore_Constantinople_clear");
        yield return new TestCaseData(1UL, "6001600055600060005500", 0, 9, 71012UL, "step:200:19800::0,0:1:0", 0).SetName("SStore_Constantinople_restore_zero");
        yield return new TestCaseData(1UL, "6000600055600160005500", 1, 9, 56012UL, "step:200:4800::0,1:0:0", 1).SetName("SStore_Constantinople_restore_one");
        yield return new TestCaseData(1UL, "6000600055600260005500", 1, 9, 56012UL, "step:200:0::0,2:0:0", 2).SetName("SStore_Constantinople_dirty_after_clear");
        yield return new TestCaseData(1UL, "600060005500", 1, 4, 26005UL, "step:5000:15000:out of gas:0,0:1:0", 1).SetName("SStore_Constantinople_clear_oog");
        yield return new TestCaseData(1UL, "600160005500", 1, 4, 21205UL, "step:200:0:out of gas:0,1:1:0", 1).SetName("SStore_Constantinople_noop_oog");
        yield return new TestCaseData(1UL, "600160005500", 1, 4, 21206UL, "step:200:0::0,1:1:0", 1).SetName("SStore_Constantinople_noop_exact");
        yield return new TestCaseData(1UL, "6001600055600060005500", 0, 9, 41211UL, "step:200:19800:out of gas:0,0:1:0", 0).SetName("SStore_Constantinople_restore_zero_oog");
        yield return new TestCaseData(1UL, "6000600055600160005500", 1, 9, 26211UL, "step:200:4800:out of gas:0,1:0:0", 1).SetName("SStore_Constantinople_restore_one_oog");
        yield return new TestCaseData(3UL, "600160005500", 1, 4, 51006UL, "step:800:0::0,1:1:0", 1).SetName("SStore_Istanbul_noop");
        yield return new TestCaseData(3UL, "600160005500", 0, 4, 51006UL, "step:20000:0::0,1:0:0", 1).SetName("SStore_Istanbul_set");
        yield return new TestCaseData(3UL, "600260005500", 1, 4, 51006UL, "step:5000:0::0,2:1:0", 2).SetName("SStore_Istanbul_reset");
        yield return new TestCaseData(3UL, "600060005500", 1, 4, 51006UL, "step:5000:15000::0,0:1:0", 0).SetName("SStore_Istanbul_clear");
        yield return new TestCaseData(3UL, "6001600055600060005500", 0, 9, 71012UL, "step:800:19200::0,0:1:0", 0).SetName("SStore_Istanbul_restore_zero");
        yield return new TestCaseData(3UL, "6000600055600160005500", 1, 9, 56012UL, "step:800:4200::0,1:0:0", 1).SetName("SStore_Istanbul_restore_one");
        yield return new TestCaseData(3UL, "6000600055600260005500", 1, 9, 56012UL, "step:800:0::0,2:0:0", 2).SetName("SStore_Istanbul_dirty_after_clear");
        yield return new TestCaseData(3UL, "600060005500", 1, 4, 26005UL, "step:5000:15000:out of gas:0,0:1:0", 1).SetName("SStore_Istanbul_clear_oog");
        yield return new TestCaseData(3UL, "600060005500", 1, 4, 23306UL, "step:0:0:out of gas: not enough gas for reentrancy sentry:0,0:1:0", 1).SetName("SStore_Istanbul_sentry");
        yield return new TestCaseData(3UL, "600160005500", 1, 4, 23307UL, "step:800:0::0,1:1:0", 1).SetName("SStore_Istanbul_above_sentry_noop");
        yield return new TestCaseData(3UL, "600160005500", 0, 4, 23307UL, "step:20000:0:out of gas:0,1:0:0", 0).SetName("SStore_Istanbul_above_sentry_set_oog");
        yield return new TestCaseData(6UL, "0x600060005500", 0, 4, 100000UL, "step:2200:0::0,0:0:0", 0).SetName("SStore_cold_zero_noop");
        yield return new TestCaseData(6UL, "0x600160005500", 0, 4, 100000UL, "step:22100:0::0,1:0:0", 1).SetName("SStore_cold_set");
        yield return new TestCaseData(6UL, "0x600160005500", 1, 4, 100000UL, "step:2200:0::0,1:1:0", 1).SetName("SStore_cold_nonzero_noop");
        yield return new TestCaseData(6UL, "0x600260005500", 1, 4, 100000UL, "step:5000:0::0,2:1:0", 2).SetName("SStore_cold_reset");
        yield return new TestCaseData(6UL, "0x600060005500", 1, 4, 100000UL, "step:5000:4800::0,0:1:0", 0).SetName("SStore_cold_clear");
        yield return new TestCaseData(6UL, "0x60005450600160005500", 1, 8, 100000UL, "step:100:0::0,1:1:0", 1).SetName("SStore_warm_noop");
        yield return new TestCaseData(6UL, "0x60005450600260005500", 1, 8, 100000UL, "step:2900:0::0,2:1:0", 2).SetName("SStore_warm_reset");
        yield return new TestCaseData(6UL, "0x60005450600060005500", 1, 8, 100000UL, "step:2900:4800::0,0:1:0", 0).SetName("SStore_warm_clear");
        yield return new TestCaseData(6UL, "0x6001600055600260005500", 0, 9, 100000UL, "step:100:0::0,2:1:0", 2).SetName("SStore_dirty_zero_replace");
        yield return new TestCaseData(6UL, "0x6001600055600060005500", 0, 9, 100000UL, "step:100:19900::0,0:1:0", 0).SetName("SStore_dirty_zero_restore");
        yield return new TestCaseData(6UL, "0x6000600055600160005500", 1, 9, 100000UL, "step:100:2800::0,1:0:0", 1).SetName("SStore_dirty_nonzero_clear_restore");
        yield return new TestCaseData(6UL, "0x6000600055600260005500", 1, 9, 100000UL, "step:100:0::0,2:0:0", 2).SetName("SStore_dirty_nonzero_clear_replace");
        yield return new TestCaseData(6UL, "0x6002600055600060005500", 1, 9, 100000UL, "step:100:4800::0,0:2:0", 0).SetName("SStore_dirty_nonzero_replace_clear");
        yield return new TestCaseData(6UL, "0x6002600055600160005500", 1, 9, 100000UL, "step:100:2800::0,1:2:0", 1).SetName("SStore_dirty_nonzero_replace_restore");
        yield return new TestCaseData(6UL, "0x600160005500", 0, 4, 23306UL, "step:0:0:out of gas: not enough gas for reentrancy sentry:0,1:0:0", 0).SetName("SStore_sentry_2300");
        yield return new TestCaseData(6UL, "0x600060005500", 0, 4, 23307UL, "step:2200:0::0,0:0:0", 0).SetName("SStore_sentry_2301_noop_success");
        yield return new TestCaseData(6UL, "0x600160005500", 0, 4, 23307UL, "step:22100:0:out of gas:0,1:0:0", 0).SetName("SStore_sentry_2301_set_oog");
        yield return new TestCaseData(6UL, "0x600060005500", 1, 4, 26005UL, "step:5000:4800:out of gas:0,0:1:0", 1).SetName("SStore_clear_oog_4999");
        yield return new TestCaseData(6UL, "0x600060005500", 1, 4, 26006UL, "step:5000:4800::0,0:1:0", 0).SetName("SStore_clear_exact_5000");
        yield return new TestCaseData(6UL, "0x60005450600060005500", 1, 8, 26010UL, "step:2900:4800:out of gas:0,0:1:0", 1).SetName("SStore_warm_clear_oog_2899");
        yield return new TestCaseData(6UL, "0x60005450600160005500", 1, 8, 25411UL, "step:0:0:out of gas: not enough gas for reentrancy sentry:0,1:1:0", 1).SetName("SStore_warm_noop_sentry_2300");
        yield return new TestCaseData(6UL, "0x55", 1, 0, 21000UL, "step:0:0:stack underflow (0 <=> 2)::1:0", 1).SetName("SStore_empty_underflow");
        yield return new TestCaseData(6UL, "0x600155", 1, 2, 21003UL, "step:0:0:stack underflow (1 <=> 2):1:1:0", 1).SetName("SStore_one_underflow");
    }
}


public class GethLikeJavaScriptStaticCallTracerTests : VirtualMachineTestsBase
{
    protected override ISpecProvider SpecProvider => new CustomSpecProvider(
        ((ForkActivation)0, Byzantium.Instance), ((ForkActivation)1, Berlin.Instance),
        ((ForkActivation)2, Prague.Instance), ((ForkActivation)3, Amsterdam.Instance));

    [Test]
    public void StaticCall_cost_is_ready_before_child_entry(
        [Values(0UL, 1UL, 2UL, 3UL)] ulong fork,
        [Values(false, true)] bool warm,
        [Values(false, true)] bool precompile,
        [Values(0, 32, 64)] int memoryLength,
        [Values(false, true)] bool maximumForwarding)
    {
        IReleaseSpec spec = SpecProvider.GetSpec((ForkActivation)fork);
        Address target = precompile ? Address.FromNumber(4) : TestItem.AddressC;
        if (!precompile)
        {
            TestState.CreateAccount(target, 1.Ether);
            TestState.InsertCode(target, Bytes.FromHexString("60006000f3"), spec);
        }
        Prepare builder = Prepare.EvmCode;
        if (warm) builder = builder.PushData(target).Op(Instruction.BALANCE).Op(Instruction.POP);
        UInt256 requested = maximumForwarding ? UInt256.MaxValue : 10000;
        byte[] code = builder.PushData(memoryLength).PushData(0).PushData(memoryLength).PushData(0)
            .PushData(target).PushData(requested).Op(Instruction.STATICCALL).Op(Instruction.STOP).Done;
        (Block block, Transaction transaction) = PrepareTx((fork, 0), 100000, code);
        ulong coldCost = spec.IsEip8038Enabled ? 3000UL : 2600UL;
        ulong access = spec.UseHotAndColdStorage ? warm || precompile ? 100UL : coldCost : spec.GasCosts.CallCost;
        ulong warmup = warm ? 5 + (spec.UseHotAndColdStorage ? precompile ? 100UL : coldCost : spec.GasCosts.BalanceCost) : 0;
        ulong entryGas = transaction.GasLimit - IntrinsicGasCalculator.Calculate(transaction, spec, block.Header.GasLimit).Standard - 18 - warmup;
        ulong intrinsic = access + (ulong)(memoryLength / 32 * 3);
        ulong forwarded = maximumForwarding ? entryGas - intrinsic - (entryGas - intrinsic) / 64 : 10000;
        string tracerCode = """
            {
                events: [],
                step: function(log) {
                    if (log.op.toNumber() === 0xfa)
                        this.events.push("step:" + log.getCost() + ":" + log.stack.length() + ":" + log.stack.peek(0).toString() + ":" + log.getGas() + ":" + log.getRefund() + ":" + log.memory.length() + ":" + (log.getError() || ""));
                },
                fault: function(log) { this.events.push("fault:" + log.getError()); },
                enter: function(frame) { this.events.push("enter:" + frame.getGas()); },
                exit: function() { this.events.push("exit"); },
                result: function() { return this.events; }
            }
            """;
        string result = RunTrace(block, transaction, tracerCode);
        Assert.That(result, Is.EqualTo(JsonSerializer.Serialize(new[] { $"step:{intrinsic + forwarded}:6:{requested}:{entryGas}:0:0:", $"enter:{forwarded}", "exit" })));
    }

    [TestCase("fa", 0UL, 100UL, "stack underflow (0 <=> 6)")]
    [TestCase("60006000600060006000fa", 15UL, 100UL, "stack underflow (5 <=> 6)")]
    [TestCase("600060006000600061beef612710fa", 117UL, 100UL, "out of gas")]
    [TestCase("600060006000600061beef612710fa", 2617UL, 100UL, "out of gas: out of gas")]
    [TestCase("6000600060017fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff61beef612710fa", 79000UL, 100UL, "gas uint64 overflow")]
    [TestCase("600060006001641fffffffe061beef612710fa", 79000UL, 100UL, "out of gas: gas uint64 overflow")]
    public void StaticCall_failures_emit_one_step_with_geth_cost(string code, ulong executionGas, ulong expectedCost, string error)
    {
        (Block block, Transaction transaction) = PrepareTx((2UL, 0), 21000 + executionGas, Bytes.FromHexString(code));
        const string tracerCode = """
            {
                events: [],
                step: function(log) { if (log.op.toNumber() === 0xfa) this.events.push("step:" + log.getCost() + ":" + (log.getError() || "")); },
                fault: function(log) { this.events.push("fault:" + log.getError()); },
                result: function() { return this.events; }
            }
            """;
        Assert.That(RunTrace(block, transaction, tracerCode),
            Is.EqualTo(JsonSerializer.Serialize(new[] { $"step:{expectedCost}:{error}" }, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping })));
    }

    private string RunTrace(Block block, Transaction transaction, string tracerCode)
    {
        using GethLikeBlockJavaScriptTracer tracer = new(TestState, SpecProvider.GetSpec(block.Header),
            GethTraceOptions.Default with { EnableMemory = true, Tracer = tracerCode });
        tracer.StartNewBlockTrace(block);
        ITxTracer transactionTracer = ((IBlockTracer)tracer).StartNewTxTrace(transaction);
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), transactionTracer);
        tracer.EndTxTrace();
        tracer.EndBlockTrace();
        using GethLikeTxTrace trace = tracer.BuildResult().First();
        return JsonSerializer.Serialize(trace.CustomTracerResult, EthereumJsonSerializer.JsonOptions);
    }
}


public class GethLikeJavaScriptCallTracerTests : VirtualMachineTestsBase
{
    protected override ISpecProvider SpecProvider => new CustomSpecProvider(
        ((ForkActivation)0, Homestead.Instance), ((ForkActivation)1, Byzantium.Instance),
        ((ForkActivation)2, Prague.Instance), ((ForkActivation)3, Amsterdam.Instance));

    [TestCaseSource(nameof(CallCases))]
    public void Call_cost_is_ready_before_transfer_and_child_entry(Instruction opcode, ulong fork, bool warm, bool newAccount,
        int value, int memoryLength, bool maximumForwarding, ulong gasLimit, bool refundPrefix)
    {
        IReleaseSpec spec = SpecProvider.GetSpec((ForkActivation)fork);
        Address target = TestItem.AddressC;
        if (!newAccount)
        {
            TestState.CreateAccount(target, 10);
            TestState.InsertCode(target, Bytes.FromHexString("00"), spec);
        }
        TestState.CreateAccount(Recipient, 1.Ether);
        TestState.Set(new StorageCell(Recipient, 0), UInt256.One);
        Prepare builder = Prepare.EvmCode;
        if (refundPrefix) builder = builder.PushData(0).PushData(0).Op(Instruction.SSTORE);
        if (warm) builder = builder.PushData(target).Op(Instruction.BALANCE).Op(Instruction.POP);
        builder = builder.PushData(memoryLength).PushData(0).PushData(memoryLength).PushData(0);
        if (opcode != Instruction.DELEGATECALL) builder = builder.PushData(value);
        UInt256 requested = maximumForwarding ? UInt256.MaxValue : 10000;
        byte[] code = builder.PushData(target).PushData(requested).Op(opcode).Op(Instruction.STOP).Done;
        (Block block, Transaction transaction) = PrepareTx((fork, 0), gasLimit, code, blockGasLimit: 40000000);
        ulong coldCost = spec.IsEip8038Enabled ? 3000UL : 2600UL;
        ulong access = spec.UseHotAndColdStorage ? warm ? 100UL : coldCost : spec.GasCosts.CallCost;
        ulong warmup = warm ? 5 + (spec.UseHotAndColdStorage ? coldCost : spec.GasCosts.BalanceCost) : 0;
        ulong clearingCost = refundPrefix ? 6 + (spec.IsEip8038Enabled ? 12100UL : 5000UL) : 0;
        ulong refund = refundPrefix ? spec.GasCosts.SClearRefund : 0;
        ulong intrinsic = IntrinsicGasCalculator.Calculate(transaction, spec, block.Header.GasLimit).Standard;
        ulong available = gasLimit - intrinsic;
        ulong reservoir = spec.IsEip8037Enabled && available > 16777216 - intrinsic ? available - (16777216 - intrinsic) : 0;
        int inputs = opcode == Instruction.DELEGATECALL ? 6 : 7;
        ulong entryGas = available - reservoir - (ulong)(inputs * 3) - warmup - clearingCost;
        bool createsAccount = opcode == Instruction.CALL && newAccount && (!spec.ClearEmptyAccountWhenTouched || value != 0);
        ulong stateSpill = createsAccount && spec.IsEip8037Enabled ? (ulong)Math.Max(0, GasCostOf.NewAccountState - (long)reservoir) : 0;
        ulong extra = access + (ulong)(memoryLength / 32 * 3) + (value != 0 ? spec.IsEip8038Enabled ? 11300UL : 9000UL : 0)
            + (createsAccount && !spec.IsEip8037Enabled ? 25000UL : 0);
        ulong forwarded = maximumForwarding ? entryGas - extra - stateSpill - (entryGas - extra - stateSpill) / 64 : 10000;
        string result = RunTrace(block, transaction, CallTracer, out GethLikeTxTrace native);
        using (native)
        using (Assert.EnterMultipleScope())
        {
            string expectedStep = $"step:{extra + forwarded}:{entryGas}:{refund}:{inputs}:{requested}:0:{(newAccount ? 0 : 10)}:{(!newAccount).ToString().ToLowerInvariant()}";
            Assert.That(result, Is.EqualTo(JsonSerializer.Serialize(new[] { expectedStep, $"enter:{forwarded + (value != 0 ? 2300UL : 0)}", "exit:0:" })));
            Assert.That(TestState.GetBalance(target), Is.EqualTo((UInt256)(uint)((newAccount ? 0 : 10) + (opcode == Instruction.CALL ? value : 0))));
            GethTxTraceEntry callEntry = native.Entries.First(entry => entry.Opcode == opcode.ToString());
            Assert.That(callEntry.GasCost, Is.EqualTo(extra + forwarded + stateSpill));
        }
    }

    [TestCase(Instruction.CALL)]
    [TestCase(Instruction.CALLCODE)]
    public void Insufficient_balance_call_has_enter_exit_without_fault(Instruction opcode)
    {
        TestState.CreateAccount(TestItem.AddressC, 10);
        TestState.InsertCode(TestItem.AddressC, Bytes.FromHexString("00"), Prague.Instance);
        byte[] code = Prepare.EvmCode.PushData(0).PushData(0).PushData(0).PushData(0).PushData(UInt256.MaxValue)
            .PushData(TestItem.AddressC).PushData(10000).Op(opcode).Op(Instruction.STOP).Done;
        (Block block, Transaction transaction) = PrepareTx((2UL, 0), 100000, code);
        string result = RunTrace(block, transaction, CallTracer, out GethLikeTxTrace native);
        using (native)
        {
            Assert.That(result, Is.EqualTo(JsonSerializer.Serialize(new[]
            {
                "step:21600:78979:0:7:10000:0:10:true", "enter:12300", "exit:0:insufficient balance for transfer"
            })));
        }
    }

    [TestCase(Instruction.CALL, 0UL, 8000UL, 1, false, 40UL, "out of gas: out of gas")]
    [TestCase(Instruction.CALLCODE, 0UL, 8000UL, 1, false, 19040UL, "out of gas")]
    [TestCase(Instruction.CALL, 0UL, 50000UL, 0, true, 40UL, "out of gas: gas uint64 overflow")]
    [TestCase(Instruction.CALLCODE, 0UL, 50000UL, 0, true, 40UL, "out of gas: gas uint64 overflow")]
    [TestCase(Instruction.DELEGATECALL, 0UL, 50000UL, 0, true, 40UL, "out of gas: gas uint64 overflow")]
    [TestCase(Instruction.CALL, 2UL, 2599UL, 0, false, 100UL, "out of gas: out of gas")]
    [TestCase(Instruction.CALLCODE, 2UL, 2599UL, 0, false, 100UL, "out of gas: out of gas")]
    [TestCase(Instruction.DELEGATECALL, 2UL, 2599UL, 0, false, 100UL, "out of gas: out of gas")]
    [TestCase(Instruction.CALL, 2UL, 8000UL, 1, false, 100UL, "out of gas: out of gas")]
    [TestCase(Instruction.CALLCODE, 2UL, 8000UL, 1, false, 100UL, "out of gas: out of gas")]
    [TestCase(Instruction.CALL, 3UL, 12000UL, 1, false, 100UL, "out of gas: out of gas")]
    [TestCase(Instruction.CALLCODE, 3UL, 12000UL, 1, false, 100UL, "out of gas: out of gas")]
    public void Call_gas_failure_keeps_fork_specific_attempted_cost(Instruction opcode, ulong fork, ulong entryGas,
        int value, bool oversizedGas, ulong expectedCost, string error)
    {
        TestState.CreateAccount(TestItem.AddressC, 10);
        TestState.InsertCode(TestItem.AddressC, Bytes.FromHexString("00"), SpecProvider.GetSpec((ForkActivation)fork));
        Prepare builder = Prepare.EvmCode.PushData(0).PushData(0).PushData(0).PushData(0);
        if (opcode != Instruction.DELEGATECALL) builder = builder.PushData(value);
        byte[] code = builder.PushData(TestItem.AddressC).PushData(oversizedGas ? UInt256.MaxValue : 10000).Op(opcode).Done;
        int inputs = opcode == Instruction.DELEGATECALL ? 6 : 7;
        (Block block, Transaction transaction) = PrepareTx((fork, 0), 100000, code);
        transaction.GasLimit = IntrinsicGasCalculator.Calculate(transaction, SpecProvider.GetSpec(block.Header), block.Header.GasLimit).Standard + (ulong)(inputs * 3) + entryGas;
        string result = RunTrace(block, transaction, ErrorTracer, out GethLikeTxTrace native);
        using (native)
            Assert.That(result, Is.EqualTo(JsonSerializer.Serialize(new[] { $"step:{expectedCost}:{error}" })));
    }

    [Test]
    public void Call_stack_underflow_precedes_gas_checks(
        [Values(Instruction.CALL, Instruction.CALLCODE, Instruction.DELEGATECALL)] Instruction opcode,
        [Values(0UL, 2UL, 3UL)] ulong fork, [Values(false, true)] bool oneMissing)
    {
        int required = opcode == Instruction.DELEGATECALL ? 6 : 7;
        int present = oneMissing ? required - 1 : 0;
        Prepare builder = Prepare.EvmCode;
        for (int i = 0; i < present; i++) builder = builder.PushData(0);
        (Block block, Transaction transaction) = PrepareTx((fork, 0), 100000, builder.Op(opcode).Done);
        transaction.GasLimit = IntrinsicGasCalculator.Calculate(transaction, SpecProvider.GetSpec(block.Header), block.Header.GasLimit).Standard + (ulong)(present * 3);
        string result = RunTrace(block, transaction, ErrorTracer, out GethLikeTxTrace native);
        using (native)
            Assert.That(result, Is.EqualTo(JsonSerializer.Serialize(new[] { $"step:{(fork == 0 ? 40 : 100)}:stack underflow ({present} <=> {required})" },
                new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping })));
    }

    [TestCase(false, 50000, false, "step:100:out of gas: write protection")]
    [TestCase(false, 2620, false, "step:100:out of gas: write protection")]
    [TestCase(false, 50000, true, "step:100:gas uint64 overflow")]
    [TestCase(true, 50000, false, "step:21600:")]
    public void Static_frame_call_value_failure_obeys_precondition_order(bool callCode, int childGas, bool invalidMemory, string expected)
    {
        TestState.CreateAccount(TestItem.AddressC, 10);
        TestState.CreateAccount(TestItem.AddressE, 10);
        TestState.InsertCode(TestItem.AddressE, Bytes.FromHexString("00"), Prague.Instance);
        byte[] child = Prepare.EvmCode.PushData(0).PushData(0).PushData(invalidMemory ? 1 : 0)
            .PushData(invalidMemory ? UInt256.MaxValue : UInt256.Zero).PushData(1).PushData(TestItem.AddressE).PushData(10000)
            .Op(callCode ? Instruction.CALLCODE : Instruction.CALL).Done;
        TestState.InsertCode(TestItem.AddressC, child, Prague.Instance);
        byte[] code = Prepare.EvmCode.PushData(0).PushData(0).PushData(0).PushData(0).PushData(TestItem.AddressC).PushData(childGas)
            .Op(Instruction.STATICCALL).Done;
        (Block block, Transaction transaction) = PrepareTx((2UL, 0), 100000, code);
        string result = RunTrace(block, transaction, ErrorTracer, out GethLikeTxTrace native);
        using (native)
            Assert.That(result, Is.EqualTo(JsonSerializer.Serialize(new[] { expected })));
    }

    [Test]
    public void Depth_rejected_call_emits_balanced_callbacks_without_fault(
        [Values(Instruction.CALL, Instruction.CALLCODE, Instruction.DELEGATECALL, Instruction.STATICCALL)] Instruction opcode)
    {
        Prepare builder = Prepare.EvmCode.PushData(0).PushData(0).PushData(0).PushData(0);
        if (opcode is Instruction.CALL or Instruction.CALLCODE) builder = builder.PushData(0);
        byte[] code = builder.PushData(Recipient).Op(Instruction.GAS).Op(opcode).Op(Instruction.STOP).Done;
        (Block block, Transaction transaction) = PrepareTx((1UL, 0), 1_000_000_000_000UL, code,
            blockGasLimit: 1_000_000_000_000UL);
        const string tracer = """
            {
                enters: 0, exits: 0, faults: 0, rejected: [],
                step: function(log) {},
                fault: function(log) { this.faults++; },
                enter: function(frame) { this.enters++; },
                exit: function(frame) {
                    this.exits++;
                    if (frame.getError()) this.rejected.push([frame.getGasUsed(), frame.getError()]);
                },
                result: function() { return [this.enters, this.exits, this.faults, this.rejected]; }
            }
            """;
        string result = RunTrace(block, transaction, tracer, out GethLikeTxTrace native);
        using (native)
            Assert.That(result, Is.EqualTo("[1025,1025,0,[[0,\"max call depth exceeded\"]]]"));
    }

    private const string ErrorTracer = """
        {
            events: [],
            step: function(log) { if ([0xf1,0xf2,0xf4].indexOf(log.op.toNumber()) >= 0) this.events.push("step:" + log.getCost() + ":" + (log.getError() || "")); },
            fault: function(log) { this.events.push("fault:" + log.getError()); },
            result: function() { return this.events; }
        }
        """;

    private string RunTrace(Block block, Transaction transaction, string tracerCode, out GethLikeTxTrace nativeResult)
    {
        using GethLikeBlockJavaScriptTracer tracer = new(TestState, SpecProvider.GetSpec(block.Header),
            GethTraceOptions.Default with { EnableMemory = true, Tracer = tracerCode });
        using GethLikeTxMemoryTracer native = new(transaction, GethTraceOptions.Default);
        tracer.StartNewBlockTrace(block);
        ITxTracer javascript = ((IBlockTracer)tracer).StartNewTxTrace(transaction);
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), new CompositeTxTracer(javascript, native));
        tracer.EndTxTrace();
        tracer.EndBlockTrace();
        using GethLikeTxTrace trace = tracer.BuildResult().First();
        nativeResult = native.BuildResult();
        return JsonSerializer.Serialize(trace.CustomTracerResult, EthereumJsonSerializer.JsonOptions);
    }

    private static IEnumerable<TestCaseData> CallCases()
    {
        foreach (Instruction opcode in new[] { Instruction.CALL, Instruction.CALLCODE, Instruction.DELEGATECALL })
            for (ulong fork = 0; fork <= 3; fork++)
                foreach (bool warm in new[] { false, true })
                    foreach (bool newAccount in new[] { false, true })
                        foreach (int value in opcode == Instruction.DELEGATECALL ? new[] { 0 } : new[] { 0, 1 })
                            foreach (int memory in new[] { 0, 32 })
                                foreach (bool maximum in fork == 0 ? new[] { false } : new[] { false, true })
                                    yield return new TestCaseData(opcode, fork, warm, newAccount, value, memory, maximum, 250000UL, false);

        foreach (Instruction opcode in new[] { Instruction.CALL, Instruction.CALLCODE, Instruction.DELEGATECALL })
            foreach (ulong fork in new[] { 0UL, 2UL, 3UL })
                yield return new TestCaseData(opcode, fork, false, false, 0, 32, false, 250000UL, true);
        yield return new TestCaseData(Instruction.CALL, 3UL, false, true, 1, 32, true, 20000000UL, false);
        yield return new TestCaseData(Instruction.CALL, 3UL, false, true, 1, 32, false, 20000000UL, false);
    }

    private const string CallTracer = """
        {
            events: [],
            step: function(log, db) {
                if ([0xf1,0xf2,0xf4].indexOf(log.op.toNumber()) < 0) return;
                var target = toAddress(log.stack.peek(1).toString(16).padStart(40, "0"));
                this.events.push("step:" + log.getCost() + ":" + log.getGas() + ":" + log.getRefund() + ":" + log.stack.length() + ":" + log.stack.peek(0).toString() + ":" + log.memory.length() + ":" + db.getBalance(target).toString() + ":" + db.exists(target));
            },
            fault: function(log) { this.events.push("fault:" + log.getError()); },
            enter: function(frame) { this.events.push("enter:" + frame.getGas()); },
            exit: function(frame) { this.events.push("exit:" + frame.getGasUsed() + ":" + (frame.getError() || "")); },
            result: function() { return this.events; }
        }
        """;
}
