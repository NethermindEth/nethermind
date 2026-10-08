// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Tracing;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Newtonsoft.Json.Linq;
using Microsoft.ClearScript;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.JavaScript;
using NSubstitute;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.GethStyle.Custom;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Call;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.FourByte;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Noop;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Prestate;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

using System.Reflection;
using JavaScriptContext = Nethermind.Blockchain.Tracing.GethStyle.Custom.JavaScript.Context;
using JavaScriptDb = Nethermind.Blockchain.Tracing.GethStyle.Custom.JavaScript.Db;

namespace Nethermind.JsonRpc.Test.Modules;

public partial class DebugRpcModuleTests
{
    [TestCase("result", 0)]
    [TestCase("result", 1)]
    [TestCase("result", 2)]
    [TestCase("null", 0)]
    [TestCase("undefined", 0)]
    [TestCase("42", 0)]
    [TestCase("step", 0)]
    [TestCase("enter", 0)]
    [TestCase("fault", 0)]
    [TestCase("fault", 1)]
    [TestCase("exit", 0)]
    [TestCase("exit", 1)]
    [TestCase("postStep", 0)]
    [TestCase("postStep", 1)]
    [TestCase("precedence", 0)]
    [TestCase("precedence", 1)]
    [TestCase("stack-input", 0)]
    [TestCase("stack-input", 1)]
    [TestCase("slice-input", 0)]
    [TestCase("slice-input", 1)]
    [TestCase("uint-input", 0)]
    [TestCase("uint-input", 1)]
    [TestCase("slice-padding", 0)]
    [TestCase("slice-padding", 1)]
    [TestCase("setup", -1)]
    [TestCase("missing", -1)]
    [TestCase("noncallable", -1)]
    [TestCase("unknown", -1)]
    [TestCase("syntax", -1)]
    public async Task Debug_traceBlockByNumber_recovers_js_errors_without_replaying_prefixes(string failure, int failAt)
    {
        JsRecoveryObservations observations = new();
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .WithConfig(new JsonRpcConfig { EnableTracingStreamMode = false })
            .Build(builder => builder.AddSingleton(observations).AddDecorator<ITransactionProcessor, JsRecoveryProcessor>());
        ulong nonce = chain.WorldStateManager.GlobalStateReader.GetNonce(chain.BlockTree.Head!.Header, TestItem.AddressB);
        byte[] runtime = Prepare.EvmCode.PushData(0).Op(Instruction.SLOAD).PushData(1).Op(Instruction.ADD)
            .PushData(0).Op(Instruction.SSTORE).STOP().Done;
        await chain.AddBlock(Build.A.Transaction.WithNonce(nonce).WithCode(Prepare.EvmCode.ForInitOf(runtime).Done)
            .WithGasLimit(1_000_000).SignedAndResolved(TestItem.PrivateKeyB).TestObject);
        Address contract = observations.Contract = ContractAddress.From(TestItem.AddressB, nonce);
        ulong firstNonce = nonce + 1;
        Address failingWrapper = ContractAddress.From(TestItem.AddressB, firstNonce + (ulong)Math.Max(failAt, 0));
        ulong failingExecutionNonce = firstNonce + (ulong)Math.Max(failAt, 0) + 1;
        byte[] call = Prepare.EvmCode.Call(contract, 100_000).STOP().Done;
        byte[] faultingCall = Prepare.EvmCode.Call(contract, 100_000).Op(Instruction.INVALID).Done;
        Transaction[] transactions = Enumerable.Range(0, 3).Select(index => Build.A.Transaction.WithNonce(firstNonce + (ulong)index)
            .WithCode(failure == "fault" && index == failAt ? faultingCall : call).WithValue((UInt256)(index + 1)).WithGasLimit(1_000_000).SignedAndResolved(TestItem.PrivateKeyB).TestObject).ToArray();
        Block block = await chain.AddBlock(transactions);
        Assert.That(block.Transactions, Has.Length.EqualTo(3));
        string resultBody = $$"""
            return {index:ctx.txIndex,storage:toHex(db.getState(toAddress('{{contract}}'),toWord('0x0'))),
                    nonce:db.getNonce(ctx.from),balance:db.getBalance(ctx.from).toString()};
            """;
        string baselineTracer = "{fault:function(){},result:function(ctx,db){" + resultBody + "}}";
        observations.Enabled = true;
        string baselineJson = await RpcTest.TestSerializedRequest(chain.DebugRpcModule, "debug_traceBlockByHash", block.Hash!, new { tracer = baselineTracer });
        JToken baseline = JToken.Parse(baselineJson);
        Assert.That(baseline["error"], Is.Null, baselineJson);
        JsRecoveryState[] expectedStates = observations.States.ToArray();
        Assert.That(expectedStates, Has.Length.EqualTo(3));
        UInt256[] expectedStorage = Enumerable.Range(0, 3)
            .Select(index => (UInt256)(index + 1 - (failure == "fault" && index >= failAt ? 1 : 0))).ToArray();
        Assert.That(expectedStates.Select(state => state.Storage), Is.EqualTo(expectedStorage));
        observations.States.Clear();
        string? invalidRead = failure switch
        {
            "stack-input" => "log.stack.peek(-1)",
            "slice-input" => "log.memory.slice(-1,1)",
            "uint-input" => "log.memory.getUint(-1)",
            "slice-padding" => "log.memory.slice(0,1048577)",
            _ => null
        };
        string tracer = failure switch
        {
            "result" => "{fault:function(){},result:function(ctx,db){if(ctx.txIndex===" + failAt + ")throw Error('result failure');" + resultBody + "}}",
            "null" or "undefined" or "42" => "{fault:function(){},result:function(ctx,db){if(ctx.txIndex===" + failAt + ")throw " + failure + ";" + resultBody + "}}",
            "step" or "postStep" => "{fault:function(){}," + failure + ":function(log,db){if(db.getNonce(toAddress('" + TestItem.AddressB + "'))===" + failingExecutionNonce + ")throw Error('" + failure + " failure');},result:function(ctx,db){" + resultBody + "}}",
            "enter" => "{fault:function(){},enter:function(frame){if(toHex(frame.getFrom())===toHex(toAddress('" + failingWrapper + "')))throw Error('enter failure');},exit:function(){},result:function(ctx,db){" + resultBody + "}}",
            "exit" => "{fault:function(){},enter:function(frame){this.fail=toHex(frame.getFrom())===toHex(toAddress('" + failingWrapper + "'));},exit:function(){if(this.fail)throw Error('exit failure');},result:function(ctx,db){" + resultBody + "}}",
            "fault" => "{fault:function(){throw Error('fault failure');},result:function(ctx,db){" + resultBody + "}}",
            "precedence" => "{fault:function(){throw Error('later fault failure');},enter:function(){},exit:function(){if(this.fail)throw Error('later exit failure');},step:function(log,db){if(this.fail)throw Error('later step failure');if(db.getNonce(toAddress('" + TestItem.AddressB + "'))===" + failingExecutionNonce + "){this.fail=true;throw Error('first failure');}},result:function(ctx,db){if(this.fail)throw Error('later result failure');" + resultBody + "}}",
            _ when invalidRead is not null => "{fault:function(){},step:function(log,db){if(db.getNonce(toAddress('" + TestItem.AddressB + "'))===" + failingExecutionNonce + "){try{" + invalidRead + ";}catch(e){}throw Error('input abort was swallowed');}},result:function(ctx,db){" + resultBody + "}}",
            "setup" => "{fault:function(){},setup:function(){throw Error('setup failure');},result:function(){return {};}}",
            "missing" => "{fault:function(){}}",
            "noncallable" => "{fault:function(){},result:42}",
            "unknown" => "unknown-js-tracer",
            _ => "{result:}"
        };
        string response = await RpcTest.TestSerializedRequest(chain.DebugRpcModule, "debug_traceBlockByNumber", block.Number, new { tracer });
        JToken actual = JToken.Parse(response);
        Assert.That(actual["error"], Is.Null, response);
        JArray entries = (JArray)actual["result"]!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries, Has.Count.EqualTo(3));
            Assert.That(observations.States.ToArray(), Is.EqualTo(expectedStates), "each transaction executes once with canonical storage, nonce and balance");
        }
        for (int index = 0; index < entries.Count; index++)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That((string?)entries[index]["txHash"], Is.EqualTo(block.Transactions[index].Hash!.ToString()));
                if (failAt < 0 || index == failAt)
                {
                    Assert.That((string?)entries[index]["error"], Is.Not.Null.And.Not.Empty, response);
                    Assert.That(entries[index]["result"], Is.Null);
                    if (invalidRead is not null)
                    {
                        string expectedError = failure switch
                        {
                            "stack-input" => "tracer accessed out of bound stack",
                            "slice-padding" => "reached limit for padding memory slice",
                            _ => "tracer accessed out of bound memory"
                        };
                        Assert.That((string?)entries[index]["error"], Does.Contain(expectedError).And.Not.Contain("input abort was swallowed"));
                    }
                    if (failure is "fault" or "exit" or "postStep" or "precedence")
                        Assert.That((string?)entries[index]["error"], Does.Contain(failure == "precedence" ? "first failure" : failure + " failure"));
                }
                else Assert.That(JToken.DeepEquals(entries[index], baseline["result"]![index]), Is.True, response);
            }
        }
        observations.Enabled = false;
        string legacy = await RpcTest.TestSerializedRequest(chain.DebugRpcModule, "debug_traceBlockByHash", block.Hash!, new { tracer });
        Assert.That(JToken.Parse(legacy)["error"], Is.Not.Null, "the recovery flag belongs only to debug_traceBlockByNumber");
        string native = await RpcTest.TestSerializedRequest(chain.DebugRpcModule, "debug_traceBlockByNumber", block.Number,
            new { tracer = "callTracer", tracerConfig = new { onlyTopCall = "not-a-boolean" } });
        Assert.That(JToken.Parse(native)["error"], Is.Not.Null, "native tracer failures remain top-level");
    }

    [Test]
    public async Task JavaScript_log_input_errors_are_uncatchable_only_in_capture_mode(
        [Values("stack.peek(-1)", "stack.peek(2147483647)", "memory.slice(-1,1)", "memory.slice(1,0)",
            "memory.getUint(-1)", "memory.getUint(0)", "memory.getUint(2147483647)")] string read, [Values] bool capture)
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        using Engine engine = new(Prague.Instance);
        using GethLikeJavaScriptTxTracer tracer = CreateTracer(engine, chain,
            "{fault:function(){},step:function(log){try{log." + read + ";}catch(e){this.caught=true;}},result:function(){return this.caught===true;}}", capture);
        tracer.SetOperationStack(default);
        using GethLikeTxTrace result = tracer.BuildResult();
        if (capture) Assert.That(result.TraceError, Does.Contain("tracer accessed out of bound"));
        else
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.TraceError, Is.Null);
                Assert.That(JsonSerializer.Serialize(result, EthereumJsonSerializer.JsonOptions), Is.EqualTo("true"));
            }
        }
    }

    [Test]
    public async Task JavaScript_log_valid_bounds_preserve_values([Values] bool capture)
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        using Engine engine = new(Prague.Instance);
        string equalBounds = capture
            ? "if(log.memory.slice(-1,-1).length!==0 || log.memory.slice(2147483648,2147483648).length!==0)throw Error('equal bounds');"
            : "";
        using GethLikeJavaScriptTxTracer tracer = CreateTracer(engine, chain, """
            {fault:function(){},step:function(log){
                if(log.stack.peek(0).toString()!=='3' || log.memory.getUint(0).toString()!=='7')throw Error('word');
                if(log.memory.slice(0,32).length!==32 || log.memory.slice(32,33)[0]!==0 || log.memory.slice(0,0).length!==0)throw Error('slice');
            """ + equalBounds + "},result:function(){return true;}}", capture);
        byte[] memory = new byte[32];
        memory[^1] = 7;
        byte[] stack = new byte[32];
        stack[^1] = 3;
        tracer.SetOperationMemory(new TraceMemory(32, memory));
        tracer.SetOperationStack(new TraceStack(stack));
        using GethLikeTxTrace result = tracer.BuildResult();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TraceError, Is.Null);
            Assert.That(JsonSerializer.Serialize(result, EthereumJsonSerializer.JsonOptions), Is.EqualTo("true"));
        }
    }

    [TestCase(0, 0, 0)]
    [TestCase(0, 0, 1)]
    [TestCase(32, 32, 0)]
    [TestCase(32, 32, 1)]
    [TestCase(32, 0, 0)]
    [TestCase(32, 0, 1)]
    [TestCase(32, 16, 0)]
    [TestCase(32, 16, 1)]
    public async Task JavaScript_log_memory_padding_limit(int memorySize, int backingSize, int excess)
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        using Engine engine = new(Prague.Instance);
        int end = memorySize + MemorySizes.MiB + excess;
        using GethLikeJavaScriptTxTracer tracer = CreateTracer(engine, chain,
            "{fault:function(){},step:function(log){this.first=log.memory.slice(0,1)[0];this.bytes=log.memory.slice(" + (end - 1) + "," + end + ");},result:function(){return {length:this.bytes.length,first:this.first,last:this.bytes[0]};}}");
        byte[] memory = new byte[backingSize];
        if (backingSize != 0) memory[0] = 7;
        tracer.SetOperationMemory(new TraceMemory((ulong)memorySize, memory));
        tracer.SetOperationStack(default);
        using GethLikeTxTrace result = tracer.BuildResult();
        using (Assert.EnterMultipleScope())
        {
            if (excess != 0)
            {
                Assert.That(result.TraceError, Is.EqualTo("reached limit for padding memory slice: 1048577"));
                Assert.That(result.CustomTracerResult, Is.Null);
            }
            else
            {
                Assert.That(result.TraceError, Is.Null);
                JToken payload = JToken.Parse(JsonSerializer.Serialize(result, EthereumJsonSerializer.JsonOptions));
                Assert.That((int?)payload["length"], Is.EqualTo(1));
                Assert.That((int?)payload["first"], Is.EqualTo(backingSize == 0 ? 0 : 7));
                Assert.That((int?)payload["last"], Is.EqualTo(0));
            }
        }
    }

    [Test]
    public async Task JavaScript_log_word_reads_use_logical_memory_size(
        [Values(MemorySizes.MiB - 32, MemorySizes.MiB)] int offset, [Values(0, 32)] int backingSize)
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        using Engine engine = new(Prague.Instance);
        using GethLikeJavaScriptTxTracer tracer = CreateTracer(engine, chain,
            "{fault:function(){},step:function(log){this.word=log.memory.getUint(" + offset + ").toString();},result:function(){return this.word;}}");
        tracer.SetOperationMemory(new TraceMemory((ulong)offset + EvmPooledMemory.WordSize, new byte[backingSize]));
        tracer.SetOperationStack(default);
        using GethLikeTxTrace result = tracer.BuildResult();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TraceError, Is.Null);
            Assert.That(JsonSerializer.Serialize(result, EthereumJsonSerializer.JsonOptions), Is.EqualTo("\"0\""));
        }
    }

    [Test]
    public async Task JavaScript_public_interrupt_escapes_full_tracer_recovery(
        [Values] bool inputError, [Values("throw Error('later JS failure');", "throw null;", "return 1;")] string completion)
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        using Engine engine = new(Prague.Instance);
        bool actionCompleted = false;
        Action interrupt = () =>
        {
            if (inputError) engine.AbortInput("invalid API input");
            engine.Interrupt();
            actionCompleted = true;
        };
        using (ScriptObject installer = (ScriptObject)engine.CreateTracer("{install:function(action){globalThis.interruptForTest=action;}}"))
            installer.InvokeMethod("install", interrupt);
        using GethLikeJavaScriptTxTracer txTracer = CreateTracer(engine, chain,
            "{fault:function(){},result:function(){interruptForTest();" + completion + "}}");
        Transaction transaction = Build.A.Transaction.WithHash(TestItem.KeccakA).TestObject;
        IBlockTracer<GethLikeTxTrace> inner = Substitute.For<IBlockTracer<GethLikeTxTrace>>();
        List<GethLikeTxTrace> completed = [];
        inner.StartNewTxTrace(transaction).Returns(txTracer);
        inner.BuildResult().Returns(completed);
        inner.When(tracer => tracer.EndTxTrace()).Do(_ => completed.Add(txTracer.BuildResult()));
        using RecoveringJavaScriptBlockTracer tracer = new(() => inner, null);
        tracer.StartNewBlockTrace(Build.A.Block.WithTransactions(transaction).TestObject);
        using ITxTracer active = tracer.StartNewTxTrace(transaction);
        ScriptInterruptedException failure = Assert.Throws<ScriptInterruptedException>(() => tracer.EndTxTrace())!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(actionCompleted, Is.True, "both requests must be recorded before returning to JS");
            Assert.That(JavaScriptTraceFailure.IsRecoverable(failure), Is.False);
            Assert.That(completed, Is.Empty, "neither tracer layer may turn cancellation into an error entry");
        }
    }

    [Test]
    public void JavaScript_input_abort_never_overrides_public_interrupt([Values(0, 1, 2)] int externalOrder)
    {
        using Engine engine = new(Prague.Instance);
        engine.PrepareNullThrowCapture();
        using ScriptObject receiver = (ScriptObject)engine.CreateTracer("{result:function(action){try{action();}catch(e){}return 'swallowed';}}");
        using ScriptObject callback = (ScriptObject)receiver.GetProperty("result");
        bool reached = false;
        bool completed = false;
        JavaScriptInputException? ownedInput = null;
        Action abort = () =>
        {
            reached = true;
            if (externalOrder == 1) engine.Interrupt();
            ownedInput = engine.AbortInput("invalid API input");
            if (externalOrder == 2) engine.Interrupt();
        };
        Exception failure = Assert.Catch(() =>
        {
            engine.InvokeCapturingNull(receiver, callback, abort, null, false, out _);
            engine.ThrowIfInputFailed();
            completed = true;
        })!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reached, Is.True);
            Assert.That(completed, Is.False, "the input failure must survive JS try/catch");
            Assert.That(engine.TryGetInputError(failure, out _), Is.EqualTo(externalOrder == 0));
            Assert.That(engine.TryGetInputError(new JavaScriptInputException("foreign input marker"), out _), Is.False);
            Assert.That(engine.TryGetInputError(new ArgumentOutOfRangeException("host state"), out _), Is.False);
            Assert.That(engine.TryGetInputError(new ScriptEngineException("host", new ArgumentOutOfRangeException("state")), out _), Is.False);
            Assert.That(ownedInput, Is.Not.Null);
            Assert.That(engine.TryGetInputError(CreateException(true, true, null, ownedInput), out _), Is.False);
            Assert.That(engine.TryGetInputError(new OperationCanceledException(), out _), Is.False);
        }
        engine.Interrupt();
        Assert.That(engine.TryGetInputError(failure, out _), Is.False);
    }

    [Test]
    public void JavaScript_input_abort_stops_a_loop_after_js_catch()
    {
        using Engine engine = new(Prague.Instance);
        engine.PrepareNullThrowCapture();
        using ScriptObject receiver = (ScriptObject)engine.CreateTracer("{result:function(action){try{action();}catch(e){}while(true){}}}");
        using ScriptObject callback = (ScriptObject)receiver.GetProperty("result");
        using CancellationTokenSource watchdog = new(TimeSpan.FromSeconds(5));
        using CancellationTokenRegistration registration = watchdog.Token.Register(engine.Interrupt);
        Action abort = () => engine.AbortInput("invalid API input");
        Exception failure = Assert.Catch(() => engine.InvokeCapturingNull(receiver, callback, abort, null, false, out _))!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(engine.TryGetInputError(failure, out string? message), Is.True);
            Assert.That(message, Is.EqualTo("invalid API input"));
        }
    }

    [Test]
    public void JavaScript_host_argument_errors_remain_unrecoverable()
    {
        using Engine engine = new(Prague.Instance);
        engine.PrepareNullThrowCapture();
        using ScriptObject receiver = (ScriptObject)engine.CreateTracer("{result:function(action){action();}}");
        using ScriptObject callback = (ScriptObject)receiver.GetProperty("result");
        Action host = () => throw new ArgumentOutOfRangeException("state");
        Exception failure = Assert.Catch(() => engine.InvokeCapturingNull(receiver, callback, host, null, false, out _))!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(engine.TryGetInputError(failure, out _), Is.False);
            Assert.That(JavaScriptTraceFailure.IsRecoverable(failure), Is.False);
        }
    }

    [Test]
    public void Debug_traceBlockByNumber_recovery_result_ownership([Values] bool transfer)
    {
        Transaction[] transactions =
        [
            Build.A.Transaction.WithHash(TestItem.KeccakA).TestObject,
            Build.A.Transaction.WithHash(TestItem.KeccakB).TestObject
        ];
        Block block = Build.A.Block.WithTransactions(transactions).TestObject;
        IBlockTracer<GethLikeTxTrace> inner = Substitute.For<IBlockTracer<GethLikeTxTrace>, IDisposable>();
        ITxTracer first = Substitute.For<ITxTracer>();
        ITxTracer second = Substitute.For<ITxTracer>();
        inner.StartNewTxTrace(Arg.Any<Transaction>()).Returns(first, second);
        List<GethLikeTxTrace> completed = [];
        IDisposable resource = Substitute.For<IDisposable>();
        inner.BuildResult().Returns(completed);
        inner.When(tracer => tracer.EndTxTrace()).Do(_ =>
        {
            if (completed.Count != 0) throw new JavaScriptTraceFailure(new ArgumentException("result failed"));
            completed.Add(new GethLikeTxTrace(resource) { TxHash = transactions[0].Hash });
        });
        IReadOnlyCollection<GethLikeTxTrace>? results = null;
        using (RecoveringJavaScriptBlockTracer tracer = new(() => inner, null))
        {
            tracer.StartNewBlockTrace(block);
            foreach (Transaction transaction in transactions)
            {
                using ITxTracer tx = tracer.StartNewTxTrace(transaction);
                tracer.EndTxTrace();
            }
            if (transfer) results = tracer.BuildResult();
        }
        first.Received(1).Dispose();
        second.Received(1).Dispose();
        ((IDisposable)inner).Received(1).Dispose();
        if (transfer)
        {
            resource.DidNotReceive().Dispose();
            Assert.That(results!.Last().TraceError, Is.EqualTo("result failed"));
            foreach (GethLikeTxTrace trace in results!) trace.Dispose();
        }
        resource.Received(1).Dispose();
    }

    [Test]
    public void Debug_traceBlockByNumber_recovery_adapter_can_be_reused([Values] bool constructionError, [Values] bool transfer)
    {
        Transaction firstTransaction = Build.A.Transaction.WithHash(TestItem.KeccakA).TestObject;
        Transaction secondTransaction = Build.A.Transaction.WithHash(TestItem.KeccakB).TestObject;
        IBlockTracer<GethLikeTxTrace> firstInner = Substitute.For<IBlockTracer<GethLikeTxTrace>, IDisposable>();
        IBlockTracer<GethLikeTxTrace> secondInner = Substitute.For<IBlockTracer<GethLikeTxTrace>, IDisposable>();
        IDisposable firstResource = Substitute.For<IDisposable>();
        IDisposable secondResource = Substitute.For<IDisposable>();
        firstInner.StartNewTxTrace(Arg.Any<Transaction>()).Returns(NullTxTracer.Instance);
        secondInner.StartNewTxTrace(Arg.Any<Transaction>()).Returns(NullTxTracer.Instance);
        firstInner.BuildResult().Returns(constructionError ? Array.Empty<GethLikeTxTrace>()
            : new[] { new GethLikeTxTrace(firstResource) { TxHash = firstTransaction.Hash } });
        secondInner.BuildResult().Returns(new[] { new GethLikeTxTrace(secondResource) { TxHash = secondTransaction.Hash } });
        int created = 0;
        IReadOnlyCollection<GethLikeTxTrace>? firstResult = null;
        IReadOnlyCollection<GethLikeTxTrace> secondResult;
        using (RecoveringJavaScriptBlockTracer tracer = new(() => ++created == 1
            ? constructionError ? throw new JavaScriptTraceFailure(new ArgumentException("first construction failed")) : firstInner
            : secondInner, null))
        {
            tracer.StartNewBlockTrace(Build.A.Block.WithTransactions(firstTransaction).TestObject);
            using (tracer.StartNewTxTrace(firstTransaction)) tracer.EndTxTrace();
            if (transfer) firstResult = tracer.BuildResult();
            tracer.StartNewBlockTrace(Build.A.Block.WithTransactions(secondTransaction).TestObject);
            if (!constructionError)
            {
                ((IDisposable)firstInner).Received(1).Dispose();
                if (transfer) firstResource.DidNotReceive().Dispose();
                else firstResource.Received(1).Dispose();
            }
            using (tracer.StartNewTxTrace(secondTransaction)) tracer.EndTxTrace();
            secondResult = tracer.BuildResult();
            Assert.That(secondResult, Has.Count.EqualTo(1));
            Assert.That(secondResult.Single().TxHash, Is.EqualTo(secondTransaction.Hash));
            Assert.That(secondResult.Single().TraceError, Is.Null);
            if (firstResult is not null)
            {
                Assert.That(firstResult.Single().TxHash, Is.EqualTo(firstTransaction.Hash));
                Assert.That(firstResult.Single().TraceError, Is.EqualTo(constructionError ? "first construction failed" : null));
            }
            tracer.Dispose();
            tracer.Dispose();
        }
        Assert.That(created, Is.EqualTo(2));
        ((IDisposable)firstInner).Received(constructionError ? 0 : 1).Dispose();
        ((IDisposable)secondInner).Received(1).Dispose();
        secondResource.DidNotReceive().Dispose();
        foreach (GethLikeTxTrace trace in secondResult) trace.Dispose();
        if (firstResult is not null)
        {
            firstResource.DidNotReceive().Dispose();
            foreach (GethLikeTxTrace trace in firstResult) trace.Dispose();
        }
        if (!constructionError) firstResource.Received(1).Dispose();
        secondResource.Received(1).Dispose();
    }

    [Test]
    public void Debug_traceBlockByNumber_recovery_does_not_contain_host_or_cancellation_errors(
        [Values("construction", "start", "end")] string phase, [Values] bool cancellation)
    {
        Exception failure = cancellation ? new OperationCanceledException() : new ScriptEngineException("host failure", new InvalidOperationException("state failed"));
        IBlockTracer<GethLikeTxTrace> inner = Substitute.For<IBlockTracer<GethLikeTxTrace>, IDisposable>();
        inner.BuildResult().Returns(Array.Empty<GethLikeTxTrace>());
        if (phase == "start") inner.StartNewTxTrace(Arg.Any<Transaction>()).Returns(_ => throw failure);
        if (phase == "end") inner.When(tracer => tracer.EndTxTrace()).Do(_ => throw failure);
        using RecoveringJavaScriptBlockTracer tracer = new(() => phase == "construction" ? throw failure : inner, null);
        Block block = Build.A.Block.TestObject;
        Action run = () =>
        {
            tracer.StartNewBlockTrace(block);
            using ITxTracer tx = tracer.StartNewTxTrace(Build.A.Transaction.WithHash(TestItem.KeccakA).TestObject);
            tracer.EndTxTrace();
        };
        Assert.That(Assert.Catch(run), Is.SameAs(failure));
    }

    private sealed class JsRecoveryObservations
    {
        internal bool Enabled;
        internal Address Contract = Address.Zero;
        internal ConcurrentQueue<JsRecoveryState> States { get; } = new();
    }

    private readonly record struct JsRecoveryState(Hash256? Hash, UInt256 Storage, ulong Nonce, UInt256 Balance);

    private sealed class JsRecoveryProcessor(ITransactionProcessor inner, IWorldState state, JsRecoveryObservations observations) : ITransactionProcessor
    {
        public TransactionResult Process(Transaction transaction, ITxTracer txTracer, ExecutionOptions options)
        {
            TransactionResult result = inner.Process(transaction, txTracer, options);
            if (observations.Enabled && transaction.SenderAddress == TestItem.AddressB && !options.HasFlag(ExecutionOptions.Warmup))
            {
                state.Get(new StorageCell(observations.Contract, 0), out UInt256 storage);
                observations.States.Enqueue(new(transaction.Hash, storage, state.GetNonce(TestItem.AddressB), state.GetBalance(TestItem.AddressB)));
            }
            return result;
        }
        public void SetBlockExecutionContext(BlockHeader blockHeader) => inner.SetBlockExecutionContext(blockHeader);
        public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext) => inner.SetBlockExecutionContext(in blockExecutionContext);
    }

    [Test]
    public async Task Debug_traceBlock_SuppliedBody_DoesNotUseIndexedHeaderState()
    {
        IParallelBlockTracer parallel = Substitute.For<IParallelBlockTracer>();
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .WithConfig(new JsonRpcConfig { EnableTracingStreamMode = false })
            .Build(builder => builder
                .AddSingleton<ISpecProvider>(new TestSpecProvider(Prague.Instance) { AllowTestChainOverride = false })
                .AddSingleton(parallel));
        ulong nonce = chain.WorldStateManager.GlobalStateReader.GetNonce(chain.BlockTree.Head!.Header, TestItem.AddressB);
        Transaction[] transactions = new Transaction[3];
        for (int i = 0; i < transactions.Length; i++)
            transactions[i] = Build.A.Transaction.WithTo(TestItem.AddressC).WithNonce(nonce + (ulong)i)
                .WithValue(1).WithGasLimit(100_000).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        Block original = await chain.AddBlock(transactions);
        GethTraceOptions options = new() { Tracer = "prestateTracer" };
        string baseline = await RpcTest.TestSerializedRequest(chain.DebugRpcModule, "debug_traceBlockByHash", original.Hash, options);
        Assert.That(parallel.ReceivedCalls(), Is.Not.Empty, "the RPC tracer must be wired to the indexed path for verified blocks");
        parallel.ClearReceivedCalls();
        Transaction[] changed = (Transaction[])original.Transactions.Clone();
        changed[0] = Build.A.Transaction.WithTo(TestItem.AddressC).WithNonce(nonce)
            .WithValue(100).WithGasLimit(100_000).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        Block supplied = new(original.Header.Clone(), changed, original.Uncles, original.Withdrawals);

        string response = await RpcTest.TestSerializedRequest(chain.DebugRpcModule, "debug_traceBlock", Rlp.Encode(supplied).ToString(), options);
        JToken expected = JToken.Parse(baseline);
        JToken actual = JToken.Parse(response);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual["error"], Is.Null);
            Assert.That(parallel.ReceivedCalls(), Is.Empty, "an unverified body must never open indexed prefixes");
            Assert.That(supplied.Hash, Is.EqualTo(original.Hash));
        }
        string sender = TestItem.AddressB.ToString();
        UInt256 before = Bytes.FromHexString(expected["result"]![1]!["result"]![sender]!["balance"]!.Value<string>()!).ToUInt256();
        UInt256 after = Bytes.FromHexString(actual["result"]![1]!["result"]![sender]!["balance"]!.Value<string>()!).ToUInt256();
        Assert.That(after, Is.EqualTo(before - 99), "the second transaction must see the modified first transfer, not the indexed prefix");
    }

    [TestCase("debug_traceBlockByHash")]
    [TestCase("debug_traceBlockByNumber")]
    public async Task Debug_traceBlock_preimage_results_preserve_transaction_hash(string method)
    {
        using Context context = await Context.Create();
        ulong nonce = context.Blockchain.WorldStateManager.GlobalStateReader.GetNonce(context.Blockchain.BlockTree.Head!.Header, TestItem.AddressB);
        Transaction transaction = Build.A.Transaction.WithTo(TestItem.AddressC).WithNonce(nonce)
            .WithValue(1).WithGasLimit(100_000).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        Block block = await context.Blockchain.AddBlock(transaction);
        object selector = method == "debug_traceBlockByHash" ? block.Hash! : block.Number;
        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, method, selector,
            new { tracer = "keccak256PreimageTracer" });
        JToken result = JToken.Parse(response);
        Assert.That(result["error"], Is.Null, response);
        Assert.That(result["result"]!.Count(), Is.EqualTo(block.Transactions.Length));
        for (int i = 0; i < block.Transactions.Length; i++)
            Assert.That(result["result"]![i]!["txHash"]!.Value<string>(), Is.EqualTo(block.Transactions[i].Hash!.ToString()));
    }

    // The supplied body keeps the stored header's hash, and the new header has no stored receipts: both must number
    // logs from the body that runs, not from receipts looked up by hash.
    [TestCase(false, false, false, TestName = "Debug_traceBlock_callTracer_log_index_follows_supplied_body_under_stored_hash")]
    [TestCase(true, false, false, TestName = "Debug_traceBlock_callTracer_log_index_follows_supplied_body_under_unstored_hash")]
    [TestCase(true, true, false, TestName = "Debug_traceBlock_callTracer_log_index_follows_supplied_body_after_reverted_tx")]
    [TestCase(false, false, true, TestName = "Debug_traceBlock_callTracer_log_index_follows_supplied_body_filtered_to_one_tx")]
    public async Task Debug_traceBlock_callTracer_log_index_follows_supplied_body(bool unstoredHeader, bool revertFirst, bool filterToSecond)
    {
        using Context context = await Context.Create();

        ulong nonce = context.Blockchain.ReadOnlyState.GetNonce(TestItem.AddressA);
        static Transaction LoggingTransaction(ulong txNonce, int logs, bool revert = false)
        {
            Prepare code = Prepare.EvmCode;
            for (int i = 0; i < logs; i++) code = code.Log(0, 0);
            code = revert ? code.Revert(0, 0) : code.STOP();
            return Build.A.Transaction.WithNonce(txNonce).WithCode(code.Done).WithGasLimit(100000)
                .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        }

        Block original = await context.Blockchain.AddBlock(LoggingTransaction(nonce, 1), LoggingTransaction(nonce + 1, 1));
        BlockHeader header = original.Header.Clone();
        if (unstoredHeader)
        {
            header.ExtraData = [0x01];
            header.Hash = header.CalculateHash();
        }
        Block supplied = new(header, [LoggingTransaction(nonce, 2, revertFirst), original.Transactions[1]], original.Uncles, original.Withdrawals);

        GethTraceOptions options = new()
        {
            Tracer = NativeCallTracer.CallTracer,
            TracerConfig = JsonSerializer.Deserialize<JsonElement>("""{"withLog":true}"""),
            TxHash = filterToSecond ? supplied.Transactions[1].Hash : null
        };
        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceBlock", Rlp.Encode(supplied).ToString(), options);
        if (filterToSecond)
        {
            string single = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceTransactionInBlockByHash",
                Rlp.Encode(supplied).ToString(), supplied.Transactions[1].Hash, options with { TxHash = null });
            Assert.That((string)JToken.Parse(single)["result"]!["logs"]![0]!["index"]!, Is.EqualTo("0x2"), single);
        }

        JToken result = JToken.Parse(response)["result"]!;
        if (filterToSecond)
        {
            Assert.That((string)result.Single()["result"]!["logs"]![0]!["index"]!, Is.EqualTo("0x2"), response);
            return;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result[0]!["result"]!["logs"]?.Select(static log => (string)log["index"]!), revertFirst ? Is.Null : Is.EqualTo(new[] { "0x0", "0x1" }), response);
            Assert.That((string)result[1]!["result"]!["logs"]![0]!["index"]!, Is.EqualTo(revertFirst ? "0x0" : "0x2"), response);
        }
    }

    [Test]
    public async Task Debug_traceBlock_callTracer_filtered_supplied_body_stops_after_target()
    {
        using Context context = await Context.Create();

        ulong nonce = context.Blockchain.ReadOnlyState.GetNonce(TestItem.AddressA);
        Transaction target = Build.A.Transaction.WithNonce(nonce).WithCode(Prepare.EvmCode.Log(0, 0).STOP().Done).WithGasLimit(100000)
            .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        Block original = await context.Blockchain.AddBlock(target);
        Transaction belowIntrinsicGas = Build.A.Transaction.WithNonce(nonce + 1).WithTo(TestItem.AddressC).WithGasLimit(1000)
            .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        Block supplied = new(original.Header.Clone(), [target, belowIntrinsicGas], original.Uncles, original.Withdrawals);

        GethTraceOptions options = new()
        {
            Tracer = NativeCallTracer.CallTracer,
            TracerConfig = JsonSerializer.Deserialize<JsonElement>("""{"withLog":true}"""),
            TxHash = target.Hash
        };
        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceBlock", Rlp.Encode(supplied).ToString(), options);

        Assert.That((string)JToken.Parse(response)["result"]!.Single()["result"]!["logs"]![0]!["index"]!, Is.EqualTo("0x0"), response);
    }

    [Test]
    public async Task Debug_traceBlock_opcode_logger_limit_resets_per_transaction([Values] bool streamMode)
    {
        using Context context = await Context.Create();
        await context.Blockchain.AddBlock(CreateTraceBlockTransactions(context.Blockchain));
        string unlimited = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceBlockByNumber",
            "latest", new { streamMode });
        string limited = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceBlockByNumber",
            "latest", new { streamMode, limit = 1 });

        JToken expected = JToken.Parse(unlimited);
        JArray transactions = (JArray)expected["result"]!;
        Assert.That(transactions, Has.Count.EqualTo(2));
        foreach (JToken transaction in transactions)
        {
            JArray entries = (JArray)transaction["result"]!["structLogs"]!;
            Assert.That(entries.Count, Is.GreaterThan(1));
            while (entries.Count > 1)
                entries.RemoveAt(entries.Count - 1);
        }

        Assert.That(JToken.DeepEquals(JToken.Parse(limited), expected), Is.True, limited);
    }

    [Test]
    public async Task Debug_traceBlock_with_invalid_rlp()
    {
        using Context context = await Context.Create();

        const string expected = """{"jsonrpc":"2.0","error":{"code":-32602,"message":"Invalid params"},"id":67}""";
        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceBlock", "xxx");

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

        string rlp = Rlp.Encode(context.Blockchain.BlockTree.Head).ToString();
        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceBlock", rlp, options);

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

        ulong blockNumber = context.Blockchain.BlockTree.Head!.Number;
        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceBlockByNumber", blockNumber, options);

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

        Hash256? blockHash = context.Blockchain.BlockTree.Head!.Hash;
        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, "debug_traceBlockByHash", blockHash, options);

        Assert.That(JsonElement.DeepEquals(
                JsonDocument.Parse(response).RootElement,
                JsonDocument.Parse(expected).RootElement),
            response);
    }

    [Test]
    public async Task Debug_traceBlockByHash_does_not_fail_bal_validation_when_tracing_osaka_block()
    {
        using Context context = await Context.Create(new TestSingleReleaseSpecProvider(Osaka.Instance));

        await context.Blockchain.AddBlock(CreateTraceBlockTransactions(context.Blockchain));

        Hash256 blockHash = context.Blockchain.BlockTree.Head!.Hash!;
        JsonRpcResponse response = await RpcTest.TestRequest(
            context.DebugRpcModule,
            "debug_traceBlockByHash",
            blockHash,
            new GethTraceOptions { Tracer = NativePrestateTracer.PrestateTracer });

        RpcTest.AssertSuccess<IReadOnlyCollection<GethLikeTxTrace>>(response);
    }

    private static IEnumerable<TestCaseData> TraceBlockSource()
    {
        Func<TestRpcBlockchain, Transaction[]> transactions = CreateTraceBlockTransactions;

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
                                { "pc": 0, "op": "PUSH32", "gas": 46536, "gasCost": 3, "depth": 1, "stack": [] },
                                { "pc": 33, "op": "PUSH1", "gas": 46533, "gasCost": 3, "depth": 1, "stack": ["0x6000602055000000000000000000000000000000000000000000000000000000"] },
                                { "pc": 35, "op": "MSTORE", "gas": 46530, "gasCost": 6, "depth": 1, "stack": ["0x6000602055000000000000000000000000000000000000000000000000000000", "0x0"] },
                                { "pc": 36, "op": "PUSH32", "gas": 46524, "gasCost": 3, "depth": 1, "stack": [] },
                                { "pc": 69, "op": "PUSH1", "gas": 46521, "gasCost": 3, "depth": 1, "stack": ["0x0"] },
                                { "pc": 71, "op": "PUSH1", "gas": 46518, "gasCost": 3, "depth": 1, "stack": ["0x0", "0x6"] },
                                { "pc": 73, "op": "PUSH1", "gas": 46515, "gasCost": 3, "depth": 1, "stack": ["0x0", "0x6", "0x0"] },
                                { "pc": 75, "op": "CREATE2", "gas": 46512, "gasCost": 32006, "depth": 1, "stack": ["0x0", "0x6", "0x0", "0x0"] },
                                { "pc": 0, "op": "PUSH1", "gas": 14280, "gasCost": 3, "depth": 2, "stack": [] },
                                { "pc": 2, "op": "PUSH1", "gas": 14277, "gasCost": 3, "depth": 2, "stack": ["0x0"] },
                                { "pc": 4, "op": "SSTORE", "gas": 14274, "gasCost": 2200, "depth": 2, "stack": ["0x0", "0x20"], "storage": { "0x0000000000000000000000000000000000000000000000000000000000000020": "0x0000000000000000000000000000000000000000000000000000000000000000" } },
                                { "pc": 5, "op": "STOP", "gas": 12074, "gasCost": 0, "depth": 2, "stack": [] },
                                { "pc": 76, "op": "STOP", "gas": 12300, "gasCost": 0, "depth": 1, "stack": ["0x28156f6fdeeffd5667d51bb8d7d5069a920e0837"] }
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
                                { "pc": 0, "op": "PUSH1", "gas": 46480, "gasCost": 3, "depth": 1, "stack": [] },
                                { "pc": 2, "op": "PUSH1", "gas": 46477, "gasCost": 3, "depth": 1, "stack": ["0x0"] },
                                { "pc": 4, "op": "PUSH1", "gas": 46474, "gasCost": 3, "depth": 1, "stack": ["0x0", "0x0"] },
                                { "pc": 6, "op": "PUSH1", "gas": 46471, "gasCost": 3, "depth": 1, "stack": ["0x0", "0x0", "0x0"] },
                                { "pc": 8, "op": "PUSH1", "gas": 46468, "gasCost": 3, "depth": 1, "stack": ["0x0", "0x0", "0x0", "0x0"] },
                                { "pc": 10, "op": "PUSH20", "gas": 46465, "gasCost": 3, "depth": 1, "stack": ["0x0", "0x0", "0x0", "0x0", "0x0"] },
                                { "pc": 31, "op": "PUSH3", "gas": 46462, "gasCost": 3, "depth": 1, "stack": ["0x0", "0x0", "0x0", "0x0", "0x0", "0x28156f6fdeeffd5667d51bb8d7d5069a920e0837"] },
                                { "pc": 35, "op": "CALL", "gas": 46459, "gasCost": 45774, "depth": 1, "stack": ["0x0", "0x0", "0x0", "0x0", "0x0", "0x28156f6fdeeffd5667d51bb8d7d5069a920e0837", "0x186a0"] },
                                { "pc": 36, "op": "STOP", "gas": 43859, "gasCost": 0, "depth": 1, "stack": ["0x1"] }
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
            new GethTraceOptions { Tracer = NativeNoopTracer.NoopTracer },
            """
            {
                "jsonrpc": "2.0",
                "result": [
                    {
                        "result": {},
                        "txHash": "0xb5a78a1eda0ae98d4f62eec3e0b7f5bf81810cd57bc75006b611982667bcdbe7"
                    },
                    {
                        "result": {},
                        "txHash": "0xdb3d8694a97364e8628aeb18993520ea6bac0b65b02eed1abddaaed1ddd04e7b"
                    }
                ],
                "id": 67
            }
            """
        )
        { TestName = "Contract with " + NativeNoopTracer.NoopTracer };

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

    private static Transaction[] CreateTraceBlockTransactions(TestRpcBlockchain blockchain)
    {
        ulong nonce = blockchain.ReadOnlyState.GetNonce(TestItem.AddressA);
        byte[] contract = Prepare.EvmCode
            .PushData(0)
            .PushData(32)
            .Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Done;

        byte[] salt = new byte[32];
        byte[] deployment = Prepare.EvmCode
            .Create2(contract, salt, 0)
            .Op(Instruction.STOP)
            .Done;

        Address deployingContractAddress = ContractAddress.From(TestItem.PrivateKeyA.Address, nonce);
        Address deploymentAddress = ContractAddress.From(deployingContractAddress, salt, contract);

        byte[] call = Prepare.EvmCode
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
    }

    [Test]
    public async Task GethLikeTxTraceStreamingResult_WriteToAsync_produces_same_json_as_serializer(
        [Values(1, 100, 1000)] int traceCount, [Values] bool errors)
    {
        List<GethLikeTxTrace> traces = new(traceCount);
        for (int i = 0; i < traceCount; i++)
        {
            GethLikeTxTrace trace = new();
            trace.TxHash = new Core.Crypto.Hash256(Keccak.Compute(i.ToString()).Bytes);
            if (errors && i % 2 == 0) trace.TraceError = "tracer failure";
            trace.Entries.Add(new GethTxTraceEntry { ProgramCounter = i, Opcode = "STOP", Gas = 21000, GasCost = 0, Depth = 1 });
            traces.Add(trace);
        }

        using GethLikeTxTraceStreamingResult result = new(traces);

        string streamedJson = await StreamToStringAsync(result);

        string stjJson = JsonSerializer.Serialize(result, EthereumJsonSerializer.JsonOptions);

        Assert.That(JsonElement.DeepEquals(
            JsonDocument.Parse(streamedJson).RootElement,
            JsonDocument.Parse(stjJson).RootElement),
            $"Streamed JSON differs from serializer output for {traceCount} traces");
        using GethLikeTxTraceCollection roundTrip = JsonSerializer.Deserialize<GethLikeTxTraceCollection>(streamedJson, EthereumJsonSerializer.JsonOptions)!;
        Assert.That(roundTrip.Traces.Select(trace => trace.TraceError), Is.EqualTo(traces.Select(trace => trace.TraceError)));
        if (errors)
        {
            JToken first = JArray.Parse(streamedJson)[0];
            using (Assert.EnterMultipleScope())
            {
                Assert.That((string?)first["error"], Is.EqualTo("tracer failure"));
                Assert.That(first["result"], Is.Null);
            }
        }
    }

    [Test]
    public async Task Streamed_call_trace_nested_to_max_call_depth_serializes([Values] bool blockResult)
    {
        using NativeCallTracerCallFrame root = new() { Type = Instruction.CALL, Error = "a<b" };
        NativeCallTracerCallFrame current = root;
        for (int depth = 1; depth < VirtualMachineStatics.MaxCallDepth; depth++)
        {
            NativeCallTracerCallFrame child = new() { Type = Instruction.CALL };
            current.Calls.Add(child);
            current = child;
        }

        GethLikeTxTrace trace = new() { TxHash = TestItem.KeccakA, CustomTracerResult = new GethLikeCustomTrace { Value = root } };
        using IDisposable result = blockResult
            ? new GethLikeTxTraceStreamingBlockResult(
                (writer, _, _) => JsonSerializer.Serialize(writer, trace, EthereumJsonSerializer.JsonOptions),
                new CancellationTokenSource(),
                LimboLogs.Instance.GetClassLogger<DebugRpcModuleTests>())
            : new GethLikeTxTraceStreamingResult([trace]);

        string streamedJson = await StreamToStringAsync((IStreamableResult)result);

        using JsonDocument document = JsonDocument.Parse(streamedJson, new JsonDocumentOptions { MaxDepth = EthereumJsonSerializer.DefaultMaxDepth });
        Assert.That(streamedJson, Does.Contain("\"error\":\"a<b\""), "string values must use the serializer's encoder");
    }

    private static async Task<string> StreamToStringAsync(IStreamableResult result)
    {
        // remove buffer limit hits from the equation by using an unbounded Pipe
        Pipe pipe = new(new PipeOptions(pauseWriterThreshold: 0));
        await result.WriteToAsync(pipe.Writer, CancellationToken.None);
        await pipe.Writer.CompleteAsync();

        ReadResult readResult = await pipe.Reader.ReadAsync();
        string streamedJson = Encoding.UTF8.GetString(readResult.Buffer);
        pipe.Reader.AdvanceTo(readResult.Buffer.End);
        return streamedJson;
    }

    [Test]
    public async Task Debug_traceBlock_returns_error_for_genesis([Values("debug_traceBlockByNumber", "debug_traceBlockByHash")] string method)
    {
        using Context context = await Context.Create();

        object? arg = method switch
        {
            "debug_traceBlockByNumber" => context.Blockchain.BlockTree.Genesis!.Number,
            _ => context.Blockchain.BlockTree.Genesis!.Hash
        };

        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, method, arg, new GethTraceOptions());

        using JsonDocument doc = JsonDocument.Parse(response);
        JsonElement root = doc.RootElement;

        Assert.That(root.TryGetProperty("error", out JsonElement error), Is.True, "Missing 'error' field");
        Assert.That(error.GetProperty("message").GetString(), Is.EqualTo("genesis is not traceable"));
        Assert.That(error.GetProperty("code").GetInt32(), Is.EqualTo(-32000));
    }

    [Test]
    public async Task Debug_traceBlock_json_rpc_request_returns_valid_json([Values("debug_traceBlock", "debug_traceBlockByNumber", "debug_traceBlockByHash")] string method)
    {
        using Context context = await Context.Create();
        await context.Blockchain.AddBlock(CreateTraceBlockTransactions(context.Blockchain));

        object? arg = method switch
        {
            "debug_traceBlock" => Rlp.Encode(context.Blockchain.BlockTree.Head).ToString(),
            "debug_traceBlockByNumber" => context.Blockchain.BlockTree.Head!.Number,
            _ => context.Blockchain.BlockTree.Head!.Hash
        };

        string response = await RpcTest.TestSerializedRequest(context.DebugRpcModule, method, arg, new GethTraceOptions());

        TestContext.Out.WriteLine(response);

        using JsonDocument doc = JsonDocument.Parse(response);
        JsonElement root = doc.RootElement;

        Assert.That(root.TryGetProperty("result", out JsonElement resultArray), Is.True, "Missing 'result' field");
        Assert.That(resultArray.ValueKind, Is.EqualTo(JsonValueKind.Array), "'result' must be an array");
        Assert.That(resultArray.GetArrayLength(), Is.GreaterThan(0), "Result array must not be empty");

        foreach (JsonElement entry in resultArray.EnumerateArray())
        {
            Assert.That(entry.TryGetProperty("result", out _), Is.True, "Each entry must have 'result'");
            Assert.That(entry.TryGetProperty("txHash", out _), Is.True, "Each entry must have 'txHash'");
        }
    }


    [Test]
    public async Task Capture_does_not_mask_opaque_getter_engine_failure()
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        using Engine engine = new(Prague.Instance);
        ScriptEngineException failure = Assert.Throws<ScriptEngineException>(() =>
        {
            using GethLikeJavaScriptTxTracer tracer = CreateTracer(engine, chain,
                "{fault:function(){},get result(){throw Error('getter failure');}}", true);
        })!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(failure.Message, Does.Contain("script exception is pending"));
            Assert.That(JavaScriptTraceFailure.IsRecoverable(failure), Is.False);
        }
    }

    [Test]
    public async Task Capture_mode_handles_null_only_at_callback_boundaries(
        [Values("setup", "result", "step", "return")] string callback, [Values] bool capture)
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        GethTraceOptions options = new()
        {
            CaptureJavaScriptErrors = capture,
            Tracer = "{fault:function(){},setup:function(){" + (callback == "setup" ? "throw null;" : "")
                + "},step:function(){" + (callback == "step" ? "throw null;" : "")
                + "},result:function(){" + (callback == "result" ? "throw null;" : "return null;") + "}}"
        };
        GethLikeTxTrace Trace()
        {
            using Engine engine = new(Prague.Instance);
            using GethLikeJavaScriptTxTracer tracer = new(engine, new JavaScriptDb(chain.MainWorldState),
                new JavaScriptContext { TxHash = TestItem.KeccakA }, options);
            if (callback == "step") tracer.SetOperationStack(default);
            return tracer.BuildResult();
        }
        if (capture || callback == "return")
        {
            using GethLikeTxTrace result = Trace();
            Assert.That(result.TraceError is null, Is.EqualTo(callback == "return"));
        }
        else Assert.Throws<ScriptEngineException>(() => Trace());
    }

    [Test]
    public async Task Result_getter_is_read_once_in_both_modes([Values] bool capture)
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        using Engine engine = new(Prague.Instance);
        using GethLikeJavaScriptTxTracer tracer = CreateTracer(engine, chain,
            "{reads:0,fault:function(){},get result(){this.reads++;return function(){return this.reads;};}}", capture);
        using GethLikeTxTrace result = tracer.BuildResult();
        Assert.That(JsonSerializer.Serialize(result, EthereumJsonSerializer.JsonOptions), Is.EqualTo("1"));
    }

    [Test]
    public async Task Capture_reads_callbacks_in_geth_order_and_retains_result_replaced_by_setup()
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        using Engine engine = new(Prague.Instance);
        using GethLikeJavaScriptTxTracer tracer = CreateTracer(engine, chain, """
            {
                order:[],
                get result(){this.order.push('result');return function(){return this.order.join(',');};},
                get fault(){this.order.push('fault');return function(){};},
                get step(){this.order.push('step');return function(){};},
                get enter(){this.order.push('enter');},
                get exit(){this.order.push('exit');},
                get postStep(){this.order.push('postStep');},
                get setup(){this.order.push('setup');return function(){
                    this.order.push('setup-call');
                    Object.defineProperty(this,'result',{value:function(){return 'replacement';}});
                };}
            }
            """);
        using GethLikeTxTrace result = tracer.BuildResult();
        Assert.That(JsonSerializer.Serialize(result, EthereumJsonSerializer.JsonOptions),
            Is.EqualTo("\"result,fault,step,enter,exit,postStep,setup,setup-call\""));
    }

    [Test]
    public async Task Capture_retains_a_self_replacing_step_callback()
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        using Engine engine = new(Prague.Instance);
        using GethLikeJavaScriptTxTracer tracer = CreateTracer(engine, chain, """
            {steps:0,fault:function(){},step:function(){
                this.steps++;this.step=function(){throw Error('replacement');};
            },result:function(){return this.steps;}}
            """);
        tracer.SetOperationStack(default);
        tracer.SetOperationStack(default);
        using GethLikeTxTrace result = tracer.BuildResult();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TraceError, Is.Null);
            Assert.That(JsonSerializer.Serialize(result, EthereumJsonSerializer.JsonOptions), Is.EqualTo("2"));
        }
    }

    [Test]
    public async Task Capture_validates_required_callbacks_and_pair_before_reading_setup([Values("result", "fault", "pair")] string missing)
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        using Engine engine = new(Prague.Instance);
        string properties = missing switch
        {
            "result" => "get fault(){throw Error('fault getter should not run');},",
            "fault" => "result:function(){},",
            _ => "result:function(){},fault:function(){},enter:function(){},"
        };
        string script = "{" + properties + "get setup(){throw Error('setup getter should not run');}}";
        JavaScriptTraceFailure failure = Assert.Throws<JavaScriptTraceFailure>(() => CreateTracer(engine, chain, script))!;
        Assert.That(failure.Message, Does.Contain(missing == "pair" ? "both or none" : "required function " + missing));
    }

    private static GethLikeJavaScriptTxTracer CreateTracer(Engine engine, TestRpcBlockchain chain, string script, bool capture = true) =>
        new(engine, new JavaScriptDb(chain.MainWorldState), new JavaScriptContext { TxHash = TestItem.KeccakA },
            new GethTraceOptions { Tracer = script, CaptureJavaScriptErrors = capture });

    [Test]
    public void Null_throw_is_observed_without_changing_the_exception_value_or_message()
    {
        using Engine engine = new(Prague.Instance);
        engine.PrepareNullThrowCapture();
        object receiver = engine.CreateTracer("{result:function(){throw null;}}");
        using ScriptObject callback = (ScriptObject)((ScriptObject)receiver).GetProperty("result");
        bool observed = false;
        ScriptEngineException captured = Assert.Throws<ScriptEngineException>(() =>
            engine.InvokeCapturingNull(receiver, callback, null, null, false, out observed))!;
        ScriptEngineException direct = Assert.Throws<ScriptEngineException>(() => ((dynamic)receiver).result(null))!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(observed, Is.True);
            Assert.That(captured.ScriptExceptionAsObject, Is.Null);
            Assert.That(captured.Message, Is.EqualTo(direct.Message));
            Assert.That(JavaScriptTraceFailure.IsRecoverable(captured, observed), Is.True);
            Assert.That(JavaScriptTraceFailure.IsRecoverable(direct), Is.False, "untagged calls retain the conservative classifier");
        }
    }

    [Test]
    public void Null_throw_does_not_mark_later_returned_null_or_host_failure()
    {
        using Engine engine = new(Prague.Instance);
        engine.PrepareNullThrowCapture();
        object receiver = engine.CreateTracer("{throwing:function(){throw null;},returned:function(){return null;},host:function(callback){callback();}}");
        using ScriptObject throwing = (ScriptObject)((ScriptObject)receiver).GetProperty("throwing");
        using ScriptObject returned = (ScriptObject)((ScriptObject)receiver).GetProperty("returned");
        using ScriptObject hostCallback = (ScriptObject)((ScriptObject)receiver).GetProperty("host");
        bool observed = false;
        Assert.Throws<ScriptEngineException>(() => engine.InvokeCapturingNull(receiver, throwing, null, null, false, out observed));
        Assert.That(observed, Is.True);
        object? result = engine.InvokeCapturingNull(receiver, returned, null, null, false, out observed);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.Null);
            Assert.That(observed, Is.False);
        }
        Assert.Throws<ScriptEngineException>(() => engine.InvokeCapturingNull(receiver, throwing, null, null, false, out observed));
        Assert.That(observed, Is.True);
        Action host = () => throw new InvalidOperationException("host state failed");
        Exception failure = Assert.Catch(() => engine.InvokeCapturingNull(receiver, hostCallback, host, null, false, out observed))!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(observed, Is.False);
            Assert.That(JavaScriptTraceFailure.IsRecoverable(failure, observed), Is.False);
        }
    }

    [Test]
    public async Task Captured_step_callbacks_keep_allocation_close_to_legacy()
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        const int calls = 1000;
        long Measure(bool capture)
        {
            using Engine engine = new(Prague.Instance);
            using GethLikeJavaScriptTxTracer tracer = CreateTracer(engine, chain,
                "{count:0,fault:function(){},step:function(){this.count++;},result:function(){return this.count;}}", capture);
            for (int i = 0; i < calls; i++) tracer.SetOperationStack(default);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < calls; i++) tracer.SetOperationStack(default);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            using GethLikeTxTrace result = tracer.BuildResult();
            Assert.That(JsonSerializer.Serialize(result, EthereumJsonSerializer.JsonOptions), Is.EqualTo("2000"));
            return allocated;
        }
        long legacy = Measure(false);
        long captured = Measure(true);
        // Allow bridge bookkeeping without accepting a fresh host delegate on each opcode.
        Assert.That(captured - legacy, Is.LessThanOrEqualTo(128L * calls));
    }

    [Test]
    public void Nested_capture_does_not_mark_the_callers_host_failure()
    {
        using Engine engine = new(Prague.Instance);
        engine.PrepareNullThrowCapture();
        using ScriptObject receiver = (ScriptObject)engine.CreateTracer("{throwing:function(){throw null;},outer:function(callback){callback();}}");
        using ScriptObject throwing = (ScriptObject)receiver.GetProperty("throwing");
        using ScriptObject outer = (ScriptObject)receiver.GetProperty("outer");
        Action nested = () =>
        {
            bool innerObserved = false;
            Assert.Throws<ScriptEngineException>(() => engine.InvokeCapturingNull(receiver, throwing, null, null, false, out innerObserved));
            Assert.That(innerObserved, Is.True);
            throw new ScriptEngineException("opaque host failure");
        };
        bool observed = false;
        Exception failure = Assert.Catch(() => engine.InvokeCapturingNull(receiver, outer, nested, null, false, out observed))!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(observed, Is.False);
            Assert.That(JavaScriptTraceFailure.IsRecoverable(failure, observed), Is.False);
            Assert.That(failure.Message, Does.Contain("opaque host failure"));
        }
    }

    [Test]
    public void Capture_preserves_receiver_getter_count_and_arguments([Values] bool twoArguments)
    {
        using Engine engine = new(Prague.Instance);
        engine.PrepareNullThrowCapture();
        object receiver = engine.CreateTracer("""
            {reads:0,marker:7,get result(){this.reads++;return function(a,b){
                return [this.marker,this.reads,arguments.length,a,b].join(':');
            };}}
            """);
        using ScriptObject callback = (ScriptObject)((ScriptObject)receiver).GetProperty("result");
        object? result = engine.InvokeCapturingNull(receiver, callback, 11, 13, twoArguments, out bool observed);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(twoArguments ? "7:1:2:11:13" : "7:1:1:11:"));
            Assert.That(observed, Is.False);
        }
    }

    [Test]
    public void Hostile_callback_cannot_reach_the_private_null_marker([Values] bool hostFailure)
    {
        using Engine engine = new(Prague.Instance);
        engine.PrepareNullThrowCapture();
        using ScriptObject receiver = (ScriptObject)engine.CreateTracer("""
            {exposed:false,count:0,result:function hostile(argument){
                this.count=hostile.arguments.length;
                const caller=hostile.caller;
                if(caller){
                    this.exposed=true;
                    const args=caller.arguments;
                    for(let i=0;args && i<args.length;i++){
                        if(args[i]!==hostile){try{args[i]();}catch(error){}}
                    }
                }
                if(argument==='ordinary')throw Error('ordinary failure');
                argument();
            }}
            """);
        using ScriptObject callback = (ScriptObject)receiver.GetProperty("result");
        Action host = () => throw new InvalidOperationException("host failure");
        object argument = hostFailure ? host : "ordinary";
        bool observed = false;
        Exception failure = Assert.Catch(() => engine.InvokeCapturingNull(receiver, callback, argument, null, false, out observed))!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(receiver.GetProperty("exposed"), Is.EqualTo(false));
            Assert.That(receiver.GetProperty("count"), Is.EqualTo(1));
            Assert.That(observed, Is.False, "caller.arguments must not expose the helper's markNull delegate");
            Assert.That(JavaScriptTraceFailure.IsRecoverable(failure, observed), Is.EqualTo(!hostFailure));
            if (!hostFailure) Assert.That(failure.Message, Does.Contain("ordinary failure"));
        }
    }

    [Test]
    public void Capture_uses_the_original_apply_intrinsic()
    {
        using Engine engine = new(Prague.Instance);
        engine.PrepareNullThrowCapture();
        object receiver = engine.CreateTracer("{replace:(Reflect.apply=function(){throw Error('hijacked');}),result:function(){return 42;}}");
        using ScriptObject callback = (ScriptObject)((ScriptObject)receiver).GetProperty("result");
        object? result = engine.InvokeCapturingNull(receiver, callback, null, null, false, out bool observed);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(42));
            Assert.That(observed, Is.False);
        }
    }

    [Test]
    public void Execution_started_alone_never_classifies_an_engine_failure_as_a_script_throw([Values] bool started)
    {
        ScriptEngineException exception = CreateException(false, started, null);
        Assert.That(JavaScriptTraceFailure.IsRecoverable(exception), Is.False);
    }

    [Test]
    public void Null_marker_never_overrides_fatal_or_host_failures([Values] bool observedNullThrow)
    {
        Exception[] failures =
        [
            CreateException(true, true, null),
            CreateException(true, true, new object()),
            CreateException(false, true, null, new InvalidOperationException("host failure")),
            CreateException(false, true, null, new TargetInvocationException(new InvalidOperationException("host failure"))),
            CreateException(false, true, null, new OperationCanceledException()),
            new ScriptInterruptedException(),
            new OperationCanceledException()
        ];
        using (Assert.EnterMultipleScope())
            foreach (Exception failure in failures)
                Assert.That(JavaScriptTraceFailure.IsRecoverable(failure, observedNullThrow), Is.False, failure.ToString());
    }

    private static ScriptEngineException CreateException(bool fatal, bool started, object? scriptValue, Exception? inner = null)
    {
        ConstructorInfo constructor = typeof(ScriptEngineException).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
            [typeof(string), typeof(string), typeof(string), typeof(int), typeof(bool), typeof(bool), typeof(object), typeof(Exception)], null)!;
        return (ScriptEngineException)constructor.Invoke(["classifier-test", "failure", "failure", 0, fatal, started, scriptValue, inner]);
    }
}
