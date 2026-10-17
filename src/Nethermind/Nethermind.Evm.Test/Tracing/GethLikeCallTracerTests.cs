// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.GethStyle.Custom;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Call;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Evm.Test.Tracing;

[TestFixture]
public class GethLikeCallTracerTests : VirtualMachineTestsBase
{
    private static readonly JsonSerializerOptions SerializerOptions = new(EthereumJsonSerializer.JsonOptionsIndented) { NewLine = "\n" };
    private static readonly IReleaseSpec CancunSpec = MainnetSpecProvider.Instance.GetSpec(MainnetSpecProvider.CancunActivation);
    // ArrayPoolList only returns a rental when its capacity is non-zero.
    private const int ProbeBufferCapacity = 32;
    internal const string? WithLog = """{"withLog":true}""";
    internal const string? OnlyTopCall = """{"onlyTopCall":true}""";
    internal const string? WithLogAndOnlyTopCall = """{"withLog":true,"onlyTopCall":true}""";

    private string ExecuteCallTrace(byte[] code, string? tracerConfig = null)
    {
        (_, Transaction tx) = PrepareTx(MainnetSpecProvider.CancunActivation, 100000, code);
        using NativeCallTracer tracer = new(tx, CancunSpec, GetGethTraceOptions(tracerConfig));
        using GethLikeTxTrace callTrace = Execute(tracer, code, MainnetSpecProvider.CancunActivation).BuildResult();
        return JsonSerializer.Serialize(callTrace.CustomTracerResult?.Value, SerializerOptions);
    }

    internal static GethTraceOptions GetGethTraceOptions(string? config) => GethTraceOptions.Default with
    {
        Tracer = NativeCallTracer.CallTracer,
        TracerConfig = config is not null ? JsonSerializer.Deserialize<JsonElement>(config) : null
    };

    [Test]
    public void Call_tracer_does_not_request_opcode_capture([Values] bool enableCapture)
    {
        Transaction tx = Build.A.Transaction.TestObject;
        GethTraceOptions options = GetGethTraceOptions(WithLog) with
        {
            DisableStack = !enableCapture,
            DisableStorage = !enableCapture,
            EnableMemory = enableCapture,
            EnableReturnData = enableCapture
        };
        using NativeCallTracer tracer = new(tx, CancunSpec, options);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.IsTracingInstructions, Is.False);
            Assert.That(tracer.IsTracingStack, Is.False);
            Assert.That(tracer.IsTracingOpLevelStorage, Is.False);
            Assert.That(tracer.IsTracingMemory, Is.False);
            Assert.That(tracer.IsTracingReturnData, Is.False);
            Assert.That(tracer.IsTracingActions, Is.True);
            Assert.That(tracer.IsTracingLogs, Is.True);
        }
    }

    [Test]
    public void Call_trace_log_indices_include_hidden_children_and_discard_reverts([Values] bool onlyTopCall, [Values] bool childReverts)
    {
        byte[] childCode = childReverts
            ? Prepare.EvmCode.Log(0, 0).Log(0, 0).Revert(0, 0).Done
            : Prepare.EvmCode.Log(0, 0).Log(0, 0).STOP().Done;
        TestState.CreateAccount(TestItem.AddressC, 0);
        TestState.InsertCode(TestItem.AddressC, childCode, Spec);
        byte[] code = Prepare.EvmCode.Log(0, 0).Call(TestItem.AddressC, 30000).Log(0, 0).STOP().Done;

        using JsonDocument trace = JsonDocument.Parse(ExecuteCallTrace(code, onlyTopCall ? WithLogAndOnlyTopCall : WithLog));
        JsonElement root = trace.RootElement;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(root.GetProperty("logs")[0].GetProperty("index").GetString(), Is.EqualTo("0x0"));
            Assert.That(root.GetProperty("logs")[1].GetProperty("index").GetString(), Is.EqualTo(childReverts ? "0x1" : "0x3"));
            if (!onlyTopCall && !childReverts)
            {
                Assert.That(root.GetProperty("calls")[0].GetProperty("logs")[0].GetProperty("index").GetString(), Is.EqualTo("0x1"));
                Assert.That(root.GetProperty("calls")[0].GetProperty("logs")[1].GetProperty("index").GetString(), Is.EqualTo("0x2"));
            }
        }
    }

    /// <summary>Forces the traced dispatch and checks that every action error is preceded by a report of no gas left.</summary>
    private sealed class GasCheckpointTracer : TxTracer
    {
        private ulong _actionGas = ulong.MaxValue;
        public override bool IsTracingInstructions => true;
        public override bool IsTracingActions => true;
        public bool HaltsLeftNoGas { get; private set; } = true;
        public int Errors { get; private set; }
        public override void ReportActionRemainingGas(ulong gas) => _actionGas = gas;

        public override void ReportActionError(EvmExceptionType evmExceptionType)
        {
            HaltsLeftNoGas &= _actionGas == 0;
            _actionGas = ulong.MaxValue;
            Errors++;
        }
    }

    public enum ChildOutcome { StackUnderflow, InvalidInstruction, InvalidJumpDestination, StackOverflow, StaticCallViolation, PrecompileFailure, OutOfGas, Revert }

    public enum FailedCreate { StackUnderflow, InvalidInstruction, DepositOutOfGas, InvalidDeposit, Revert }

    public enum TraceDispatch { Untraced, Cancelable, Traced }

    private const ulong ChildGas = 10000;
    private const ulong RevertGas = 6; // PUSH1 0, PUSH1 0, REVERT

    [Test(Description = "Like geth, a call frame that halts on anything but REVERT reports all its gas as used, while the root reports the transaction's gasUsed")]
    public void Failed_child_frame_reports_gas_used([Values] ChildOutcome outcome, [Values(1, 2)] int depth, [Values] TraceDispatch dispatch)
    {
        Address child = outcome == ChildOutcome.PrecompileFailure ? new Address("0x0000000000000000000000000000000000000009") : TestItem.AddressC;
        // The 1025th push overflows the 1024-item stack.
        byte[] pushes = new byte[1025];
        pushes.AsSpan().Fill((byte)Instruction.PUSH0);
        byte[] childCode = outcome switch
        {
            ChildOutcome.StackUnderflow => [(byte)Instruction.ADD],
            ChildOutcome.InvalidInstruction => [(byte)Instruction.INVALID],
            ChildOutcome.InvalidJumpDestination => Prepare.EvmCode.PushData(0).Op(Instruction.JUMP).Done,
            ChildOutcome.StackOverflow => pushes,
            ChildOutcome.StaticCallViolation => Prepare.EvmCode.PushData(1).PushData(0).Op(Instruction.SSTORE).Done,
            ChildOutcome.OutOfGas => Prepare.EvmCode.Op(Instruction.JUMPDEST).PushData(0).Op(Instruction.JUMP).Done,
            ChildOutcome.Revert => Prepare.EvmCode.Revert(0, 0).Done,
            _ => [] // blake2f rejects an input that is not 213 bytes long
        };
        if (childCode.Length != 0)
        {
            TestState.CreateAccount(child, 0);
            TestState.InsertCode(child, childCode, Spec);
        }

        Prepare callChild = outcome == ChildOutcome.StaticCallViolation
            ? Prepare.EvmCode.StaticCall(child, (long)ChildGas)
            : Prepare.EvmCode.Call(child, (long)ChildGas);
        byte[] code = callChild.Op(Instruction.POP).STOP().Done;
        if (depth == 2)
        {
            TestState.CreateAccount(TestItem.AddressD, 0);
            TestState.InsertCode(TestItem.AddressD, code, Spec);
            code = Prepare.EvmCode.Call(TestItem.AddressD, 50000).Op(Instruction.POP).STOP().Done;
        }

        using GethLikeTxTrace trace = TraceFailedFrame(code, dispatch, outcome == ChildOutcome.Revert ? 0 : 1, out ulong spentGas);
        NativeCallTracerCallFrame root = (NativeCallTracerCallFrame)trace.CustomTracerResult!.Value!;
        NativeCallTracerCallFrame frame = depth == 1 ? root.Calls.AssertSingle() : root.Calls.AssertSingle().Calls.AssertSingle();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(frame.To, Is.EqualTo(child), "to");
            Assert.That(frame.Gas, Is.EqualTo(ChildGas), "gas");
            Assert.That(frame.GasUsed, Is.EqualTo(outcome == ChildOutcome.Revert ? RevertGas : ChildGas), "gasUsed");
            Assert.That(frame.Error, Is.Not.Null, "error");
            if (outcome == ChildOutcome.Revert)
            {
                Assert.That(frame.Error, Is.EqualTo("execution reverted"), "revert error");
            }

            if (depth == 2)
            {
                Assert.That(root.Calls[0].Error, Is.Null, "the middle frame survives its child's failure");
            }

            Assert.That(root.GasUsed, Is.EqualTo(spentGas), "the root reports the transaction's gasUsed");
        }
    }

    [Test(Description = "Like geth, a CREATE frame that halts or fails its code deposit reports all its gas as used")]
    public void Failed_create_frame_reports_gas_used([Values] FailedCreate outcome, [Values] TraceDispatch dispatch)
    {
        byte[] initCode = outcome switch
        {
            FailedCreate.StackUnderflow => [(byte)Instruction.ADD],
            FailedCreate.InvalidInstruction => [(byte)Instruction.INVALID],
            FailedCreate.DepositOutOfGas => Prepare.EvmCode.ForInitOf(new byte[1024]).Done,
            FailedCreate.InvalidDeposit => Prepare.EvmCode.ForInitOf([0xEF]).Done,
            _ => Prepare.EvmCode.Revert(0, 0).Done
        };
        byte[] code = Prepare.EvmCode.Create(initCode, 0).Op(Instruction.POP).STOP().Done;

        using GethLikeTxTrace trace = TraceFailedFrame(code, dispatch, outcome == FailedCreate.Revert ? 0 : 1, out ulong spentGas);
        NativeCallTracerCallFrame root = (NativeCallTracerCallFrame)trace.CustomTracerResult!.Value!;
        NativeCallTracerCallFrame frame = root.Calls.AssertSingle();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(frame.Type, Is.EqualTo(Instruction.CREATE), "type");
            Assert.That(frame.GasUsed, Is.EqualTo(outcome == FailedCreate.Revert ? RevertGas : frame.Gas), "gasUsed");
            Assert.That(frame.Error, Is.Not.Null, "error");
            if (outcome == FailedCreate.Revert)
            {
                Assert.That(frame.Error, Is.EqualTo("execution reverted"), "revert error");
            }

            Assert.That(root.GasUsed, Is.EqualTo(spentGas), "the root reports the transaction's gasUsed");
        }
    }

    /// <summary>Traces <paramref name="code"/> with the callTracer through the chosen dispatch, and measures the gas
    /// the transaction spends with no tracer observing it.</summary>
    /// <remarks>The traced dispatch runs beside a <see cref="GasCheckpointTracer"/>, which checks the action errors.</remarks>
    private GethLikeTxTrace TraceFailedFrame(byte[] code, TraceDispatch dispatch, int expectedErrors, out ulong spentGas)
    {
        (Block block, Transaction tx) = PrepareTx(MainnetSpecProvider.CancunActivation, 100000, code);

        GasSpentTracer receipt = new();
        _processor.CallAndRestore(tx, block.Header, receipt);
        spentGas = receipt.SpentGas;

        NativeCallTracer native = new(tx, CancunSpec, GetGethTraceOptions(null));
        GasCheckpointTracer checkpoints = new();
        // Disposing a wrapper disposes the callTracer too, so the result is built first.
        using ITxTracer tracer = dispatch switch
        {
            TraceDispatch.Untraced => native,
            TraceDispatch.Cancelable => new CancellationTxTracer(native),
            _ => new CompositeTxTracer(native, checkpoints)
        };
        _processor.CallAndRestore(tx, block.Header, tracer);

        if (dispatch == TraceDispatch.Traced)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(checkpoints.Errors, Is.EqualTo(expectedErrors), "action errors");
                Assert.That(checkpoints.HaltsLeftNoGas, Is.True, "every action error follows a report of no gas left");
            }
        }

        return native.BuildResult();
    }

    /// <summary>Records the gas the transaction spends, which the receipt carries, without tracing any action.</summary>
    private sealed class GasSpentTracer : TxTracer
    {
        public ulong SpentGas { get; private set; }
        public override bool IsTracingReceipt => true;

        public override void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null) =>
            SpentGas = gasSpent.SpentGas;

        public override void MarkAsFailed(Address recipient, in GasConsumed gasSpent, byte[] output, string? error, Hash256? stateRoot = null) =>
            SpentGas = gasSpent.SpentGas;
    }

    [Test]
    public void Test_CallTrace_SingleCall()
    {
        byte[] code = Prepare.EvmCode
            .PushData(SampleHexData1.PadLeft(64, '0'))
            .PushData(0)
            .Op(Instruction.SSTORE)
            .PushData(SampleHexData2.PadLeft(64, '0'))
            .PushData(32)
            .Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Done;

        string callTrace = ExecuteCallTrace(code);
        const string expectedCallTrace = """
{
  "type": "CALL",
  "from": "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099",
  "to": "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
  "value": "0x1",
  "gas": "0x186a0",
  "gasUsed": "0xfebc",
  "input": "0x"
}
""";
        Assert.That(callTrace, Is.EqualTo(expectedCallTrace));
    }

    [Test]
    public void Test_CallTrace_NestedCalls()
    {
        byte[] code = CreateNestedCallsCode();
        string callTrace = ExecuteCallTrace(code);
        const string expectedCallTrace = """
{
  "type": "CALL",
  "from": "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099",
  "to": "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
  "value": "0x1",
  "gas": "0x186a0",
  "gasUsed": "0x16644",
  "input": "0x",
  "calls": [
    {
      "type": "CALL",
      "from": "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
      "to": "0x76e68a8696537e4141926f3e528733af9e237d69",
      "value": "0x0",
      "gas": "0xc350",
      "gasUsed": "0x8400",
      "input": "0xa01234",
      "calls": [
        {
          "type": "CREATE",
          "from": "0x76e68a8696537e4141926f3e528733af9e237d69",
          "to": "0xd75a3a95360e44a3874e691fb48d77855f127069",
          "value": "0x0",
          "gas": "0x4513",
          "gasUsed": "0x26a",
          "input": "0x7f000000000000000000000000000000000000000000000000000000000000000060005260036000f3",
          "output": "0x000000"
        }
      ]
    },
    {
      "type": "DELEGATECALL",
      "from": "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
      "to": "0x76e68a8696537e4141926f3e528733af9e237d69",
      "value": "0x1",
      "gas": "0xa342",
      "gasUsed": "0x8400",
      "input": "0x",
      "calls": [
        {
          "type": "CREATE",
          "from": "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
          "to": "0x89aa9b2ce05aaef815f25b237238c0b4ffff6ae3",
          "value": "0x0",
          "gas": "0x2585",
          "gasUsed": "0x26a",
          "input": "0x7f000000000000000000000000000000000000000000000000000000000000000060005260036000f3",
          "output": "0x000000"
        }
      ]
    }
  ]
}
""";
        Assert.That(callTrace, Is.EqualTo(expectedCallTrace));
    }

    [Test]
    public void Test_CallTrace_NestedCalls_WithLog()
    {
        byte[] code = CreateNestedCallsCode();
        string callTrace = ExecuteCallTrace(code, WithLog);
        const string expectedCallTrace = """
{
  "type": "CALL",
  "from": "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099",
  "to": "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
  "value": "0x1",
  "gas": "0x186a0",
  "gasUsed": "0x16644",
  "input": "0x",
  "logs": [
    {
      "address": "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
      "data": "0x",
      "topics": [],
      "index": "0x2",
      "position": "0x2"
    }
  ],
  "calls": [
    {
      "type": "CALL",
      "from": "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
      "to": "0x76e68a8696537e4141926f3e528733af9e237d69",
      "value": "0x0",
      "gas": "0xc350",
      "gasUsed": "0x8400",
      "input": "0xa01234",
      "logs": [
        {
          "address": "0x76e68a8696537e4141926f3e528733af9e237d69",
          "data": "0x",
          "topics": ["0x1f675bff07515f5df96737194ea945c36c41e7b4fcef307b7cd4d0e602a69111","0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760"
          ],
          "index": "0x0",
          "position": "0x1"
        }
      ],
      "calls": [
        {
          "type": "CREATE",
          "from": "0x76e68a8696537e4141926f3e528733af9e237d69",
          "to": "0xd75a3a95360e44a3874e691fb48d77855f127069",
          "value": "0x0",
          "gas": "0x4513",
          "gasUsed": "0x26a",
          "input": "0x7f000000000000000000000000000000000000000000000000000000000000000060005260036000f3",
          "output": "0x000000"
        }
      ]
    },
    {
      "type": "DELEGATECALL",
      "from": "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
      "to": "0x76e68a8696537e4141926f3e528733af9e237d69",
      "value": "0x1",
      "gas": "0xa342",
      "gasUsed": "0x8400",
      "input": "0x",
      "logs": [
        {
          "address": "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
          "data": "0x",
          "topics": ["0x1f675bff07515f5df96737194ea945c36c41e7b4fcef307b7cd4d0e602a69111","0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760"
          ],
          "index": "0x1",
          "position": "0x1"
        }
      ],
      "calls": [
        {
          "type": "CREATE",
          "from": "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
          "to": "0x89aa9b2ce05aaef815f25b237238c0b4ffff6ae3",
          "value": "0x0",
          "gas": "0x2585",
          "gasUsed": "0x26a",
          "input": "0x7f000000000000000000000000000000000000000000000000000000000000000060005260036000f3",
          "output": "0x000000"
        }
      ]
    }
  ]
}
""";
        Assert.That(callTrace, Is.EqualTo(expectedCallTrace));
    }

    [Test]
    public void Test_CallTrace_NestedCalls_OnlyTopCall()
    {
        byte[] code = CreateNestedCallsCode();
        string callTrace = ExecuteCallTrace(code, OnlyTopCall);
        const string expectedCallTrace = """
{
  "type": "CALL",
  "from": "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099",
  "to": "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
  "value": "0x1",
  "gas": "0x186a0",
  "gasUsed": "0x16644",
  "input": "0x"
}
""";
        Assert.That(callTrace, Is.EqualTo(expectedCallTrace));
    }

    [Test]
    public void Test_CallTrace_NestedCalls_WithLogsAndOnlyTopCall()
    {
        byte[] code = CreateNestedCallsCode();
        string callTrace = ExecuteCallTrace(code, WithLogAndOnlyTopCall);
        const string expectedCallTrace = """
{
  "type": "CALL",
  "from": "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099",
  "to": "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
  "value": "0x1",
  "gas": "0x186a0",
  "gasUsed": "0x16644",
  "input": "0x",
  "logs": [
    {
      "address": "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
      "data": "0x",
      "topics": [],
      "index": "0x2",
      "position": "0x0"
    }
  ]
}
""";
        Assert.That(callTrace, Is.EqualTo(expectedCallTrace));
    }

    [Test]
    public void Test_CallTrace_WithLog_LogIndexStart_OffsetsIndex()
    {
        byte[] code = CreateNestedCallsCode();
        (_, Transaction tx) = PrepareTx(MainnetSpecProvider.CancunActivation, 100000, code);
        using NativeCallTracer tracer = new(tx, CancunSpec, GetGethTraceOptions(WithLog) with { LogIndex = new BlockLogIndex(5) });
        using GethLikeTxTrace trace = Execute(tracer, code, MainnetSpecProvider.CancunActivation).BuildResult();

        NativeCallTracerCallFrame topFrame = (NativeCallTracerCallFrame)trace.CustomTracerResult!.Value!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(topFrame.Calls[0].Logs!.AssertSingle().Index, Is.EqualTo(5UL));
            Assert.That(topFrame.Calls[1].Logs!.AssertSingle().Index, Is.EqualTo(6UL));
            Assert.That(topFrame.Logs!.AssertSingle().Index, Is.EqualTo(7UL));
        }
    }

    [Test]
    public void Test_CallTrace_NestedCalls_RevertParentCall()
    {
        byte[] code = CreateNestedCallsCode(true);
        string callTrace = ExecuteCallTrace(code, WithLog);
        const string expectedCallTrace = """
{
  "type": "CALL",
  "from": "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099",
  "to": "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
  "value": "0x1",
  "gas": "0x186a0",
  "gasUsed": "0x1664a",
  "input": "0x",
  "error": "execution reverted",
  "calls": [
    {
      "type": "CALL",
      "from": "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
      "to": "0x76e68a8696537e4141926f3e528733af9e237d69",
      "value": "0x0",
      "gas": "0xc350",
      "gasUsed": "0x8400",
      "input": "0xa01234",
      "calls": [
        {
          "type": "CREATE",
          "from": "0x76e68a8696537e4141926f3e528733af9e237d69",
          "to": "0xd75a3a95360e44a3874e691fb48d77855f127069",
          "value": "0x0",
          "gas": "0x4513",
          "gasUsed": "0x26a",
          "input": "0x7f000000000000000000000000000000000000000000000000000000000000000060005260036000f3",
          "output": "0x000000"
        }
      ]
    },
    {
      "type": "DELEGATECALL",
      "from": "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
      "to": "0x76e68a8696537e4141926f3e528733af9e237d69",
      "value": "0x1",
      "gas": "0xa342",
      "gasUsed": "0x8400",
      "input": "0x",
      "calls": [
        {
          "type": "CREATE",
          "from": "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
          "to": "0x89aa9b2ce05aaef815f25b237238c0b4ffff6ae3",
          "value": "0x0",
          "gas": "0x2585",
          "gasUsed": "0x26a",
          "input": "0x7f000000000000000000000000000000000000000000000000000000000000000060005260036000f3",
          "output": "0x000000"
        }
      ]
    }
  ]
}
""";
        Assert.That(callTrace, Is.EqualTo(expectedCallTrace));
    }

    [Test]
    public void Test_CallTrace_NestedCalls_RevertInternalCall()
    {
        byte[] code = CreateNestedCallsCode(false, true);
        string callTrace = ExecuteCallTrace(code, WithLog);
        const string expectedCallTrace = """
{
  "type": "CALL",
  "from": "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099",
  "to": "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
  "value": "0x1",
  "gas": "0x186a0",
  "gasUsed": "0x16650",
  "input": "0x",
  "logs": [
    {
      "address": "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
      "data": "0x",
      "topics": [],
      "index": "0x0",
      "position": "0x2"
    }
  ],
  "calls": [
    {
      "type": "CALL",
      "from": "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
      "to": "0x76e68a8696537e4141926f3e528733af9e237d69",
      "value": "0x0",
      "gas": "0xc350",
      "gasUsed": "0x8406",
      "input": "0xa01234",
      "error": "execution reverted",
      "calls": [
        {
          "type": "CREATE",
          "from": "0x76e68a8696537e4141926f3e528733af9e237d69",
          "to": "0xd75a3a95360e44a3874e691fb48d77855f127069",
          "value": "0x0",
          "gas": "0x4513",
          "gasUsed": "0x26a",
          "input": "0x7f000000000000000000000000000000000000000000000000000000000000000060005260036000f3",
          "output": "0x000000"
        }
      ]
    },
    {
      "type": "DELEGATECALL",
      "from": "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
      "to": "0x76e68a8696537e4141926f3e528733af9e237d69",
      "value": "0x1",
      "gas": "0xa33c",
      "gasUsed": "0x8406",
      "input": "0x",
      "error": "execution reverted",
      "calls": [
        {
          "type": "CREATE",
          "from": "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
          "to": "0x89aa9b2ce05aaef815f25b237238c0b4ffff6ae3",
          "value": "0x0",
          "gas": "0x257f",
          "gasUsed": "0x26a",
          "input": "0x7f000000000000000000000000000000000000000000000000000000000000000060005260036000f3",
          "output": "0x000000"
        }
      ]
    }
  ]
}
""";
        Assert.That(callTrace, Is.EqualTo(expectedCallTrace));
    }

    [Test(Description = "A nested frame that catches a child's revert and keeps running must stay successful and keep the log it emits")]
    public void Test_CallTrace_NestedCall_CatchesChildRevert_KeepsPostCatchLog()
    {
        Address revertAddress = TestItem.AddressC;
        byte[] revertCode = Prepare.EvmCode.Revert(0, 0).Done;
        TestState.CreateAccount(revertAddress, 0);
        TestState.InsertCode(revertAddress, revertCode, Spec);

        Address catchAddress = TestItem.AddressD;
        byte[] catchAndLogCode = Prepare.EvmCode.Call(TestItem.AddressC, 30000).Log(0, 0).STOP().Done;
        TestState.CreateAccount(catchAddress, 0);
        TestState.InsertCode(catchAddress, catchAndLogCode, Spec);

        byte[] txCode = Prepare.EvmCode.Call(catchAddress, 60000).STOP().Done;
        (_, Transaction tx) = PrepareTx(MainnetSpecProvider.CancunActivation, 100000, txCode);
        using NativeCallTracer tracer = new(tx, CancunSpec, GetGethTraceOptions(WithLog));
        using GethLikeTxTrace trace = Execute(tracer, txCode, MainnetSpecProvider.CancunActivation).BuildResult();

        NativeCallTracerCallFrame topFrame = (NativeCallTracerCallFrame)trace.CustomTracerResult!.Value!;
        NativeCallTracerCallFrame catchFrame = topFrame.Calls.AssertSingle();
        NativeCallTracerCallFrame revertFrame = catchFrame.Calls.AssertSingle();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(topFrame.Error, Is.Null);
            Assert.That(topFrame.Logs, Is.Null);

            Assert.That(catchFrame.To, Is.EqualTo(catchAddress));
            Assert.That(catchFrame.Error, Is.Null, "a frame that swallows a child revert and returns must stay successful");
            Assert.That(catchFrame.Logs, Is.Not.Null, "the post-catch log belongs to the catcher frame");

            NativeCallTracerLogEntry catcherLog = catchFrame.Logs.AssertSingle();
            Assert.That(catcherLog.Address, Is.EqualTo(catchAddress));
            Assert.That(catcherLog.Position, Is.EqualTo(1UL));
            Assert.That(catcherLog.Index, Is.EqualTo(0UL));

            Assert.That(revertFrame.To, Is.EqualTo(revertAddress));
            Assert.That(revertFrame.Error, Is.EqualTo("execution reverted"));
            Assert.That(revertFrame.Logs, Is.Null);
        }
    }

    [Test]
    public void Test_CallTrace_NestedCalls_RevertAllCalls()
    {
        byte[] code = CreateNestedCallsCode(true, true);
        string callTrace = ExecuteCallTrace(code, WithLog);
        const string expectedCallTrace = """
{
  "type": "CALL",
  "from": "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099",
  "to": "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
  "value": "0x1",
  "gas": "0x186a0",
  "gasUsed": "0x16656",
  "input": "0x",
  "error": "execution reverted",
  "calls": [
    {
      "type": "CALL",
      "from": "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
      "to": "0x76e68a8696537e4141926f3e528733af9e237d69",
      "value": "0x0",
      "gas": "0xc350",
      "gasUsed": "0x8406",
      "input": "0xa01234",
      "error": "execution reverted",
      "calls": [
        {
          "type": "CREATE",
          "from": "0x76e68a8696537e4141926f3e528733af9e237d69",
          "to": "0xd75a3a95360e44a3874e691fb48d77855f127069",
          "value": "0x0",
          "gas": "0x4513",
          "gasUsed": "0x26a",
          "input": "0x7f000000000000000000000000000000000000000000000000000000000000000060005260036000f3",
          "output": "0x000000"
        }
      ]
    },
    {
      "type": "DELEGATECALL",
      "from": "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
      "to": "0x76e68a8696537e4141926f3e528733af9e237d69",
      "value": "0x1",
      "gas": "0xa33c",
      "gasUsed": "0x8406",
      "input": "0x",
      "error": "execution reverted",
      "calls": [
        {
          "type": "CREATE",
          "from": "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
          "to": "0x89aa9b2ce05aaef815f25b237238c0b4ffff6ae3",
          "value": "0x0",
          "gas": "0x257f",
          "gasUsed": "0x26a",
          "input": "0x7f000000000000000000000000000000000000000000000000000000000000000060005260036000f3",
          "output": "0x000000"
        }
      ]
    }
  ]
}
""";
        Assert.That(callTrace, Is.EqualTo(expectedCallTrace));
    }

    public enum RejectedAction { CallBalance, CreateBalance, Create2Collision }

    [Test]
    public void Test_CallTrace_RejectedAction_RecordsFailedFrame([Values] RejectedAction scenario)
    {
        byte[] initCode = Prepare.EvmCode.ForInitOf(new byte[3]).Done;
        byte[] salt = Bytes.FromHexString("0x01").PadLeft(32);
        TestState.CreateAccount(TestItem.AddressC, 1.Ether);
        TestState.InsertCode(TestItem.AddressC, Prepare.EvmCode.Op(Instruction.STOP).Done, Spec);
        // The transaction runs at AddressB; an account with code already sits at its CREATE2 address.
        Address collision = ContractAddress.From(TestItem.AddressB, salt, initCode);
        TestState.CreateAccount(collision, 0);
        TestState.InsertCode(collision, Prepare.EvmCode.Op(Instruction.STOP).Done, Spec);
        Prepare rejectedAction = scenario switch
        {
            RejectedAction.CallBalance => Prepare.EvmCode.CallWithValue(TestItem.AddressC, 50000, 1000.Ether),
            RejectedAction.CreateBalance => Prepare.EvmCode.Create(initCode, 1000.Ether),
            _ => Prepare.EvmCode.Create2(initCode, salt, 0),
        };
        byte[] code = rejectedAction.Op(Instruction.POP).STOP().Done;

        using JsonDocument trace = JsonDocument.Parse(ExecuteCallTrace(code));

        JsonElement calls = trace.RootElement.GetProperty("calls");
        JsonElement rejected = calls[0];
        bool collided = scenario == RejectedAction.Create2Collision;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(calls.GetArrayLength(), Is.EqualTo(1), "calls");
            Assert.That(rejected.GetProperty("type").GetString(), Is.EqualTo(scenario switch
            {
                RejectedAction.CallBalance => "CALL",
                RejectedAction.CreateBalance => "CREATE",
                _ => "CREATE2",
            }), "type");
            Assert.That(rejected.GetProperty("error").GetString(),
                Is.EqualTo(collided ? "contract address collision" : "insufficient balance for transfer"), "error");
            // A failed precheck returns all the gas; a collision consumes it.
            Assert.That(rejected.GetProperty("gasUsed").GetString(),
                Is.EqualTo(collided ? rejected.GetProperty("gas").GetString() : "0x0"), "gasUsed");
            Assert.That(rejected.TryGetProperty("to", out _), Is.EqualTo(scenario == RejectedAction.CallBalance), "to");
            Assert.That(rejected.TryGetProperty("calls", out _), Is.False, "nested calls");
        }
    }

    [Test]
    public void Test_CallTrace_SelfDestruct()
    {
        byte[] code = Prepare.EvmCode
            .PushData(TestItem.AddressA)
            .Op(Instruction.SELFDESTRUCT)
            .Done;

        string callTrace = ExecuteCallTrace(code);
        const string expectedCallTrace = """
            {
              "type": "CALL",
              "from": "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099",
              "to": "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
              "value": "0x1",
              "gas": "0x186a0",
              "gasUsed": "0x6593",
              "input": "0x",
              "calls": [
                {
                  "type": "SELFDESTRUCT",
                  "from": "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
                  "to": "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099",
                  "value": "0xad78ebc5ac6200001",
                  "gas": "0x0",
                  "gasUsed": "0x0",
                  "input": "0x"
                }
              ]
            }
            """;
        Assert.That(callTrace, Is.EqualTo(expectedCallTrace));
    }

    [Test]
    public void Test_CallTrace_SelfDestruct_OnlyTopCall()
    {
        byte[] code = Prepare.EvmCode
            .PushData(TestItem.AddressA)
            .Op(Instruction.SELFDESTRUCT)
            .Done;

        string callTrace = ExecuteCallTrace(code, OnlyTopCall);
        const string expectedCallTrace = """
            {
              "type": "CALL",
              "from": "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099",
              "to": "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358",
              "value": "0x1",
              "gas": "0x186a0",
              "gasUsed": "0x6593",
              "input": "0x"
            }
            """;
        Assert.That(callTrace, Is.EqualTo(expectedCallTrace));
    }

    public enum CreateOutcome { Success, Revert, InvalidOpcode, AddressCollision }

    [TestCase(CreateOutcome.Success, TestName = "TopLevelCreate_Success")]
    [TestCase(CreateOutcome.Revert, TestName = "TopLevelCreate_Revert")]
    [TestCase(CreateOutcome.InvalidOpcode, TestName = "TopLevelCreate_InvalidOpcode")]
    [TestCase(CreateOutcome.AddressCollision, TestName = "TopLevelCreate_AddressCollision")]
    public void Test_CallTrace_TopLevelCreate(CreateOutcome outcome)
    {
        byte[] initCode;
        if (outcome == CreateOutcome.AddressCollision)
        {
            initCode = Prepare.EvmCode.PushData(0).PushData(0).Op(Instruction.RETURN).Done;
            Address deploymentAddress = ContractAddress.From(Sender, TestState.GetNonce(Sender));
            TestState.CreateAccount(deploymentAddress, 0);
            TestState.InsertCode(deploymentAddress, new byte[] { 0xEF }, SpecProvider.GenesisSpec);
            TestState.Commit(SpecProvider.GenesisSpec);
        }
        else
        {
            initCode = outcome switch
            {
                CreateOutcome.Revert => Prepare.EvmCode.PushData(0).PushData(0).Op(Instruction.REVERT).Done,
                CreateOutcome.InvalidOpcode => Prepare.EvmCode.Op(Instruction.INVALID).Done,
                _ => Prepare.EvmCode.ForInitOf(new byte[3]).Done
            };
        }

        (Block block, Transaction tx) = PrepareInitTx(MainnetSpecProvider.CancunActivation, 100000, initCode);
        using NativeCallTracer tracer = new(tx, CancunSpec, GetGethTraceOptions(null));
        _processor.Execute(tx, new BlockExecutionContext(block.Header, SpecProvider.GetSpec((block.Header.Number, block.Header.Timestamp))), tracer);
        using GethLikeTxTrace trace = tracer.BuildResult();

        if (outcome == CreateOutcome.AddressCollision)
        {
            Assert.That(trace.CustomTracerResult, Is.Null,
                "address-collision path never enters the EVM; no call frame should be produced");
            return;
        }

        NativeCallTracerCallFrame? frame = trace.CustomTracerResult?.Value as NativeCallTracerCallFrame;
        Assert.That(frame, Is.Not.Null, "expected a top-level CREATE call frame");
        Assert.That(frame!.Type, Is.EqualTo(Instruction.CREATE));
        if (outcome == CreateOutcome.Success)
        {
            Assert.That(frame.Error, Is.Null, "expected no error on successful CREATE");
            Assert.That(frame.To, Is.Not.Null, "expected deployed contract address");
        }
        else
        {
            Assert.That(frame.Error, Is.Not.Null, "expected error description on a halted CREATE");
            Assert.That(frame.To, Is.Null, "a failed CREATE deploys no contract, so `to` must be omitted");
        }
    }

    [Test(Description = "A top-level CREATE whose code deposit fails reports no gas left before its error")]
    public void Top_level_create_deposit_failure_reports_no_gas_left([Values] bool invalidCode)
    {
        byte[] initCode = Prepare.EvmCode.ForInitOf(invalidCode ? new byte[] { 0xEF } : new byte[1024]).Done;
        (Block block, Transaction tx) = PrepareInitTx(MainnetSpecProvider.CancunActivation, 100000, initCode);
        GasCheckpointTracer checkpoints = new();

        _processor.Execute(tx, new BlockExecutionContext(block.Header, SpecProvider.GetSpec((block.Header.Number, block.Header.Timestamp))), checkpoints);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(checkpoints.Errors, Is.EqualTo(1), "action errors");
            Assert.That(checkpoints.HaltsLeftNoGas, Is.True, "every action error follows a report of no gas left");
        }
    }

    [Test]
    public void Test_CallTrace_MarkAsFailed_WithoutEvmError_NoCrash()
    {
        Transaction tx = Build.A.Transaction.WithGasLimit(100000).WithData([0x00]).TestObject;
        using NativeCallTracer tracer = new(tx, CancunSpec, GetGethTraceOptions(null));

        tracer.ReportAction(100000, 0, TestItem.AddressA, TestItem.AddressB, ReadOnlyMemory<byte>.Empty, ExecutionType.CREATE);
        tracer.ReportActionEnd(40000, TestItem.AddressB, new byte[] { 0xEF });

        Assert.That(
            () => tracer.MarkAsFailed(TestItem.AddressB, new GasConsumed(60000, 60000), [], "deploy failed post-EVM"),
            Throws.Nothing);

        using GethLikeTxTrace trace = tracer.BuildResult();
        NativeCallTracerCallFrame? frame = trace.CustomTracerResult?.Value as NativeCallTracerCallFrame;
        Assert.That(frame, Is.Not.Null, "expected a top-level call frame (ReportAction populated _callStack)");
        Assert.That(frame!.Type, Is.EqualTo(Instruction.CREATE));
        Assert.That(frame.Error, Is.Null, "no EVM exception fired; Error must stay null when _error is null");
    }

    [Test]
    public void Test_CallTrace_TopLevelCreate_WithLog_DeployContractFailure_LogsCleared()
    {
        byte[] initCode = Prepare.EvmCode
            .PushData(0)
            .PushData(0)
            .Op(Instruction.LOG0)
            .PushData(0xEF)
            .PushData(0)
            .Op(Instruction.MSTORE8)
            .PushData(1)
            .PushData(0)
            .Op(Instruction.RETURN)
            .Done;

        (Block block, Transaction tx) = PrepareInitTx(MainnetSpecProvider.CancunActivation, 100000, initCode);
        using NativeCallTracer tracer = new(tx, CancunSpec, GetGethTraceOptions(WithLog));
        _processor.Execute(tx, new BlockExecutionContext(block.Header, SpecProvider.GetSpec((block.Header.Number, block.Header.Timestamp))), tracer);
        using GethLikeTxTrace trace = tracer.BuildResult();

        NativeCallTracerCallFrame? frame = trace.CustomTracerResult?.Value as NativeCallTracerCallFrame;
        Assert.That(frame, Is.Not.Null, "expected a top-level call frame (EVM ran before deployment was rejected)");
        Assert.That(frame!.Logs, Is.Null, "logs must be cleared on a failed CREATE frame even when _error is null");
    }

    [Test]
    public void Test_CallTrace_ReportLog_EmptyCallStack_DoesNotThrow()
    {
        // A frame transaction running entirely through default code emits an EIP-7708 transfer log
        // without any ReportAction, so callTracer sees an empty call stack.
        Transaction tx = Build.A.Transaction.TestObject;
        using NativeCallTracer tracer = new(tx, CancunSpec, GetGethTraceOptions(WithLog));

        Assert.That(
            () => tracer.ReportLog(new LogEntry(TestItem.AddressA, [], [])),
            Throws.Nothing);
    }

    [Test]
    public void Test_CallTrace_EveryTopLevelFrame_IsReturnedToThePool()
    {
        // A plain transaction, deliberately: only a root BuildResult leaves behind can be skipped, and a
        // frame transaction's roots are folded into the one synthetic root the trace takes ownership of.
        Transaction tx = Build.A.Transaction.WithGasLimit(100000).TestObject;
        NativeCallTracer tracer = new(tx, CancunSpec, GetGethTraceOptions(null));

        // Two top-level invocations, which is what a transaction running more than one frame produces:
        // each frame enters at depth 0, so each leaves its own root on the call stack.
        ReportTopLevelInvocation(tracer, TestItem.AddressB);
        ReportTopLevelInvocation(tracer, TestItem.AddressC);

        NativeCallTracerCallFrame[] roots = TopLevelFramesOf(tracer);
        Assert.That(roots, Has.Length.EqualTo(2), "both top-level invocations should be on the call stack");

        tracer.MarkAsSuccess(TestItem.AddressB, new GasConsumed(21000, 21000), [], []);
        GethLikeTxTrace trace = tracer.BuildResult();

        TrackingPool[] pools = new TrackingPool[roots.Length];
        for (int i = 0; i < roots.Length; i++)
        {
            pools[i] = AttachRentalProbe(roots[i]);
        }

        tracer.Dispose();
        trace.Dispose();

        using (Assert.EnterMultipleScope())
        {
            for (int i = 0; i < roots.Length; i++)
            {
                Assert.That(pools[i].Returned, Has.Count.EqualTo(1), $"top-level frame {i} never returned its pooled buffers");
            }
        }
    }

    private static void ReportTopLevelInvocation(NativeCallTracer tracer, Address to)
    {
        tracer.ReportAction(50000, 1, TestItem.AddressA, to, ReadOnlyMemory<byte>.Empty, ExecutionType.CALL);
        tracer.ReportActionEnd(40000ul, ReadOnlyMemory<byte>.Empty);
    }

    private static NativeCallTracerCallFrame[] TopLevelFramesOf(NativeCallTracer tracer) =>
        ((ArrayPoolList<NativeCallTracerCallFrame>)typeof(NativeCallTracer)
            .GetField("_callStack", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(tracer)!).AsSpan().ToArray();

    /// <summary>Gives a call frame a buffer rented from a pool of its own, so that whether the frame was
    /// disposed can be read off that pool rather than inferred from the frame.</summary>
    /// <returns>The pool, which receives the rental back when the frame is disposed.</returns>
    /// <remarks>The frame's own buffers come from the shared pool and it exposes no seam to swap that, so the
    /// probe is attached through <see cref="NativeCallTracerCallFrame.Logs"/>. Disposing a frame disposes
    /// every buffer it holds, so a returned rental here means the whole frame went back.</remarks>
    private static TrackingPool AttachRentalProbe(NativeCallTracerCallFrame callFrame)
    {
        TrackingPool pool = new();
        callFrame.Logs?.Dispose();
        callFrame.Logs = new ArrayPoolList<NativeCallTracerLogEntry>(pool, ProbeBufferCapacity);
        return pool;
    }

    private sealed class TrackingPool : ArrayPool<NativeCallTracerLogEntry>
    {
        public List<NativeCallTracerLogEntry[]> Returned { get; } = [];
        public override NativeCallTracerLogEntry[] Rent(int minimumLength) => new NativeCallTracerLogEntry[minimumLength];
        public override void Return(NativeCallTracerLogEntry[] array, bool clearArray = false) => Returned.Add(array);
    }

    [Test]
    public void Test_CallTrace_NestedInputAndOutput_SurviveCallerMemoryReuse([Values] bool childReverts)
    {
        byte[] txData = [0xaa, 0xbb];
        byte[] callerMemory = [1, 2, 3, 4, 5];
        byte[] txOutput = [0xcc];
        Transaction tx = Build.A.Transaction.WithGasLimit(100000).WithData(txData).TestObject;
        using NativeCallTracer tracer = new(tx, CancunSpec, GetGethTraceOptions(null));

        tracer.ReportAction(100000, 0, TestItem.AddressA, TestItem.AddressB, txData, ExecutionType.TRANSACTION);
        tracer.ReportAction(50000, 0, TestItem.AddressB, TestItem.AddressC, callerMemory.AsMemory(0, 4), ExecutionType.CALL);
        if (childReverts) tracer.ReportActionRevert(30000, callerMemory.AsMemory(1, 4));
        else tracer.ReportActionEnd(30000, callerMemory.AsMemory(1, 4));
        callerMemory.AsSpan().Fill(0xff);
        tracer.ReportActionEnd(10000, ReadOnlyMemory<byte>.Empty);
        tracer.MarkAsSuccess(TestItem.AddressB, new GasConsumed(21000, 21000), txOutput, []);

        using GethLikeTxTrace trace = tracer.BuildResult();
        NativeCallTracerCallFrame root = (NativeCallTracerCallFrame)trace.CustomTracerResult!.Value;
        NativeCallTracerCallFrame child = root.Calls[0];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(root.Input.ToArray(), Is.EqualTo(txData));
            Assert.That(root.Output.ToArray(), Is.EqualTo(txOutput));
            Assert.That(child.Input.ToArray(), Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
            Assert.That(child.Output.ToArray(), Is.EqualTo(new byte[] { 2, 3, 4, 5 }));
        }
    }

    private static GethLikeTxTrace TraceAmsterdamTopCall(bool withSubFrame)
    {
        IReleaseSpec spec = Substitute.For<IReleaseSpec>();
        spec.IsEip8037Enabled.Returns(true);
        Transaction tx = Build.A.Transaction.WithGasLimit(100000).TestObject;
        using NativeCallTracer tracer = new(tx, spec, GetGethTraceOptions(null));

        tracer.ReportAction(100000, 1, TestItem.AddressA, TestItem.AddressB, ReadOnlyMemory<byte>.Empty, ExecutionType.CALL);
        if (withSubFrame)
        {
            tracer.ReportAction(50000, 0, TestItem.AddressB, TestItem.AddressC, ReadOnlyMemory<byte>.Empty, ExecutionType.CALL);
            tracer.ReportActionEnd(30000, ReadOnlyMemory<byte>.Empty);
        }
        tracer.ReportActionEnd(withSubFrame ? 10000ul : 40000ul, ReadOnlyMemory<byte>.Empty);
        tracer.MarkAsSuccess(TestItem.AddressB, new GasConsumed(21000, 21000, 25000, 5000, 30000, 9000), [], []);

        return tracer.BuildResult();
    }

    [Test]
    public void Test_CallTrace_Amsterdam_TopFrame_IncludesTwoDimensionalGas()
    {
        using GethLikeTxTrace trace = TraceAmsterdamTopCall(withSubFrame: false);
        NativeCallTracerCallFrame frame = (NativeCallTracerCallFrame)trace.CustomTracerResult!.Value;
        string json = JsonSerializer.Serialize(trace.CustomTracerResult.Value, SerializerOptions);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(frame.Eip8037Gas, Is.EqualTo(new TwoDimensionalGas(25000, 5000, 9000)));
            Assert.That(json, Does.Contain("""
              "regularGasUsed": "0x61a8",
              "stateGasUsed": "0x1388",
              "gasRefund": "0x2328",
            """.ReplaceLineEndings("\n")));
        }
    }

    [Test]
    public void Test_CallTrace_Amsterdam_SubFrames_OmitTwoDimensionalGas()
    {
        using GethLikeTxTrace trace = TraceAmsterdamTopCall(withSubFrame: true);
        NativeCallTracerCallFrame frame = (NativeCallTracerCallFrame)trace.CustomTracerResult!.Value;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(frame.Eip8037Gas, Is.Not.Null, "top frame carries the two-dimensional gas");
            Assert.That(frame.Calls, Has.Count.EqualTo(1));
            Assert.That(frame.Calls[0].Eip8037Gas, Is.Null, "sub-frames must omit the two-dimensional gas");
        }
    }

    [Test]
    public void Test_CallTrace_DeepNesting_DoesNotThrow()
    {
        using NativeCallTracerCallFrame root = BuildLinearCallChain(VirtualMachineStatics.MaxCallDepth);
        GethLikeCustomTrace customTrace = new() { Value = root };

        Assert.That(
            () => JsonSerializer.Serialize(customTrace, EthereumJsonSerializer.JsonOptions),
            Throws.Nothing);
    }

    [Test]
    public void Test_CallTrace_LeafFrame_OmitsCalls([Values] bool callsTouched)
    {
        using NativeCallTracerCallFrame frame = BuildLinearCallChain(1);
        if (callsTouched)
        {
            Assert.That(frame.Calls, Is.Empty);
        }

        string json = JsonSerializer.Serialize(frame, EthereumJsonSerializer.JsonOptions);

        Assert.That(json, Is.EqualTo("""{"type":"CALL","from":"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099","to":"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358","gas":"0x186a0","gasUsed":"0xc350","input":"0x"}"""));
        Assert.That(frame.HasCalls, Is.EqualTo(callsTouched), "serializing a leaf frame must not allocate its calls list");
    }

    [Test]
    public void Test_CallTrace_DeepNesting_StreamedJsonIsComplete()
    {
        int depth = VirtualMachineStatics.MaxCallDepth;
        using NativeCallTracerCallFrame root = BuildLinearCallChain(depth);
        GethLikeCustomTrace customTrace = new() { Value = root };

        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { SkipValidation = true, MaxDepth = EthereumJsonSerializer.DefaultMaxDepth }))
        {
            JsonSerializer.Serialize(writer, customTrace, EthereumJsonSerializer.JsonOptions);
        }

        string output = Encoding.UTF8.GetString(stream.ToArray());

        using JsonDocument document = JsonDocument.Parse(output, new JsonDocumentOptions { MaxDepth = EthereumJsonSerializer.DefaultMaxDepth });
        JsonElement element = document.RootElement;
        int observed = 1;
        while (element.TryGetProperty("calls", out JsonElement calls))
        {
            Assert.That(calls.GetArrayLength(), Is.EqualTo(1), $"frame at depth {observed} should have one child");
            element = calls[0];
            observed++;
        }
        Assert.That(observed, Is.EqualTo(depth), "deserialized frame chain depth should match the constructed depth");
    }

    [Test]
    public void Test_CallTrace_DeepNesting_FailsBeyondMaxDepth()
    {
        int boundary = EthereumJsonSerializer.DefaultMaxDepth / 2;

        using NativeCallTracerCallFrame atBoundary = BuildLinearCallChain(boundary);
        Assert.That(
            () => JsonSerializer.Serialize(new GethLikeCustomTrace { Value = atBoundary }, EthereumJsonSerializer.JsonOptions),
            Throws.Nothing,
            $"chain of {boundary} frames must serialize");

        using NativeCallTracerCallFrame justOver = BuildLinearCallChain(boundary + 1);
        Assert.That(
            () => JsonSerializer.Serialize(new GethLikeCustomTrace { Value = justOver }, EthereumJsonSerializer.JsonOptions),
            Throws.TypeOf<JsonException>()
                .With.InnerException.TypeOf<InvalidOperationException>()
                .And.InnerException.Message.EqualTo(
                    $"CurrentDepth ({EthereumJsonSerializer.DefaultMaxDepth}) is equal to or larger than the maximum allowed depth of {EthereumJsonSerializer.DefaultMaxDepth}. Cannot write the next JSON object or array."),
            $"chain of {boundary + 1} frames must throw the writer's depth-too-large error");
    }

    private static NativeCallTracerCallFrame BuildLinearCallChain(int depth)
    {
        NativeCallTracerCallFrame root = new()
        {
            Type = Instruction.CALL,
            From = TestItem.AddressA,
            To = TestItem.AddressB,
            Gas = 100_000,
            GasUsed = 50_000,
        };
        NativeCallTracerCallFrame current = root;
        for (int i = 1; i < depth; i++)
        {
            NativeCallTracerCallFrame child = new()
            {
                Type = Instruction.CALL,
                From = TestItem.AddressA,
                To = TestItem.AddressB,
                Gas = 100_000,
                GasUsed = 50_000,
            };
            current.Calls.Add(child);
            current = child;
        }
        return root;
    }

    private byte[] CreateNestedCallsCode(bool revertParentCall = false, bool revertCreateCall = false)
    {
        byte[] deployedCode = new byte[3];

        byte[] initCode = Prepare.EvmCode.ForInitOf(deployedCode).Done;

        Prepare createCodePrepare = Prepare.EvmCode
            .Create(initCode, 0)
            .Log(0, 0, [TestItem.KeccakA, TestItem.KeccakB]);
        byte[] createCode = revertCreateCall ? createCodePrepare.Revert(0, 0).Done : createCodePrepare.STOP().Done;

        TestState.CreateAccount(TestItem.AddressC, 1.Ether);
        TestState.InsertCode(TestItem.AddressC, createCode, Spec);
        Prepare callCodePrepare = Prepare.EvmCode
            .CallWithInput(TestItem.AddressC, 50000, SampleHexData1)
            .DelegateCall(TestItem.AddressC, 50000)
            .Log(0, 0);
        return revertParentCall ? callCodePrepare.Revert(0, 0).Done : callCodePrepare.Done;
    }
}
