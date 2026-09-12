// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test.Tracing;

[TestFixture]
public class InstructionTracingFilterTests : VirtualMachineTestsBase
{
    [Test]
    public void Execute_WhenMaskChanges_ObservesTheCurrentMask([Values] bool reverse)
    {
        byte[] code = Prepare.EvmCode
            .PushData(2)
            .PushData(3)
            .Op(Instruction.ADD)
            .PushData(4)
            .Op(Instruction.MUL)
            .Op(Instruction.STOP)
            .Done;
        Instruction first = reverse ? Instruction.MUL : Instruction.ADD;
        Instruction second = reverse ? Instruction.ADD : Instruction.MUL;
        foreach (Instruction selected in new[] { first, second, first })
        {
            using FilteredTracer tracer = new(selected);
            Execute(tracer, code, MainnetSpecProvider.CancunActivation);

            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success), "each transaction must execute successfully");
            Assert.That(tracer.Operations, Does.Contain(selected), "the current mask must select its opcode");
            Assert.That(tracer.Operations, Does.Not.Contain(selected == first ? second : first), "the cached previous mask must not leak observations");
        }
    }

    [Test]
    public void Execute_WhenOpcodeActivationChanges_UsesCurrentFork([Values] bool activatedFirst)
    {
        byte[] code = Prepare.EvmCode
            .Op(Instruction.PUSH0)
            .Op(Instruction.STOP)
            .Done;
        foreach (bool activated in new[] { activatedFirst, !activatedFirst, activatedFirst })
        {
            using FilteredTracer tracer = new(Instruction.PUSH0);
            Execute(tracer, code, (MainnetSpecProvider.ParisBlockNumber + 1,
                MainnetSpecProvider.ShanghaiBlockTimestamp - (activated ? 0UL : 1UL)));

            Assert.That(tracer.StatusCode, Is.EqualTo(activated ? StatusCode.Success : StatusCode.Failure), "PUSH0 must follow the current fork");
            Assert.That(tracer.OperationError, Is.EqualTo(activated ? null : (EvmExceptionType?)EvmExceptionType.BadInstruction), "the pre-activation transaction must reject PUSH0");
            Assert.That(tracer.Operations, Does.Contain(Instruction.PUSH0), "selected opcodes must remain observable across fork changes");
        }
    }

    [Test]
    public void Execute_WhenCancellationModeChanges_UsesCurrentDispatch([Values] bool cancelableFirst)
    {
        byte[] code = new byte[2049];
        Array.Fill(code, (byte)Instruction.JUMPDEST);
        code[^1] = (byte)Instruction.STOP;
        using FilteredTracer warmup = new(Instruction.JUMPDEST, cancelableFirst);
        Execute(warmup, code, MainnetSpecProvider.CancunActivation);
        Assert.That(warmup.StatusCode, Is.EqualTo(StatusCode.Success), "populate the cache with the first cancellation mode");

        using FilteredTracer next = new(Instruction.JUMPDEST, !cancelableFirst, cancelOnOperation: true);
        Assert.That(next.CancellationRequested, Is.False, "cancellation must happen after dispatch begins");
        if (cancelableFirst)
        {
            Execute(next, code, MainnetSpecProvider.CancunActivation);
            Assert.That(next.StatusCode, Is.EqualTo(StatusCode.Success), "noncancelable dispatch must not retain cancellation handlers");
            Assert.That(next.Operations.Count, Is.EqualTo(2048), "a stale cancelable handler must not truncate execution at its batch boundary");
        }
        else
        {
            Assert.Throws<OperationCanceledException>(() => Execute(next, code, MainnetSpecProvider.CancunActivation),
                "cancelable dispatch must observe cancellation requested during execution");
        }
        Assert.That(next.Operations, Does.Contain(Instruction.JUMPDEST), "the filtered callback must execute before cancellation");
        Assert.That(next.CancellationRequested, Is.True, "the callback must request cancellation deterministically");
        Assert.That(next.CancellationPolls > 0, Is.EqualTo(!cancelableFirst), "only cancelable execution may poll cancellation");
    }

    private sealed class FilteredTracer(Instruction selected, bool cancelable = false, bool cancelOnOperation = false)
        : TestAllTracerWithOutput, IInstructionTracingFilter, ITxTracer
    {
        public UInt256 InstructionMask => UInt256.One << (int)selected;
        public List<Instruction> Operations { get; } = [];
        public EvmExceptionType? OperationError { get; private set; }
        public bool CancellationRequested { get; private set; }
        public int CancellationPolls { get; private set; }
        bool ITxTracer.IsCancelable => cancelable;
        bool ITxTracer.IsCancelled
        {
            get
            {
                CancellationPolls++;
                return CancellationRequested;
            }
        }

        public override void ReportOperationError(EvmExceptionType error) => OperationError = error;

        public override void StartOperation(int pc, Instruction opcode, ulong gas, in ExecutionEnvironment env)
        {
            Operations.Add(opcode);
            CancellationRequested |= cancelOnOperation;
        }
    }
}
