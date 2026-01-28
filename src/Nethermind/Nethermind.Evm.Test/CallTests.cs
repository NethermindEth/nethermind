// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    public class CallTests : VirtualMachineTestsBase
    {
        protected override long BlockNumber => MainnetSpecProvider.ParisBlockNumber;
        protected override ulong Timestamp => MainnetSpecProvider.OsakaBlockTimestamp;

        [Test]
        [TestCase(Instruction.CALL)]
        [TestCase(Instruction.CALLCODE)]
        [TestCase(Instruction.DELEGATECALL)]
        [TestCase(Instruction.STATICCALL)]
        public void Stack_underflow_on_call(Instruction instruction)
        {
            byte[] code = Prepare.EvmCode
                .PushData(0)
                .PushData(0)
                .PushData("0x805e0d3cde3764a4d0a02f33cf624c8b7cfd911a")
                .PushData("0x793d1e")
                .Op(instruction)
                .Done;

            TestAllTracerWithOutput result = Execute(Activation, 21020, code);
            Assert.That(result.Error, Is.EqualTo("StackUnderflow"));
        }

        [Test]
        [TestCase(Instruction.CALL)]
        [TestCase(Instruction.CALLCODE)]
        [TestCase(Instruction.DELEGATECALL)]
        [TestCase(Instruction.STATICCALL)]
        public void Out_of_gas_on_call(Instruction instruction)
        {
            byte[] code = Prepare.EvmCode
                .PushData(0)
                .PushData(0)
                .PushData("0x805e0d3cde3764a4d0a02f33cf624c8b7cfd911a")
                .PushData("0x793d1e")
                .PushData("0x793d1e")
                .PushData("0x793d1e")
                .PushData("0x793d1e")
                .Op(instruction)
                .Done;

            TestAllTracerWithOutput result = Execute(Activation, 21020, code);
            Assert.That(result.Error, Is.EqualTo("OutOfGas"));
        }

        /// <summary>
        /// Regression: HandleTopLevelFailure gated ReportActionError on TTracingInst
        /// instead of TTracingActions, so action-only tracers missed error reports.
        /// </summary>
        [Test]
        public void Action_only_tracer_receives_ReportActionError_on_top_level_failure()
        {
            // INVALID opcode causes a top-level exception
            byte[] code = Prepare.EvmCode
                .Op(Instruction.INVALID)
                .Done;

            ActionOnlyTracer tracer = Execute(new ActionOnlyTracer(), code);

            Assert.That(tracer.ReportedActionErrors, Has.Count.EqualTo(1));
            Assert.That(tracer.ReportedActionErrors[0], Is.EqualTo(EvmExceptionType.BadInstruction));
        }

        /// <summary>
        /// Regression: same as above but for a nested call that fails — the parent frame's
        /// action tracer should see the nested failure reported.
        /// </summary>
        [Test]
        public void Action_only_tracer_receives_ReportActionError_on_nested_failure()
        {
            // Deploy a contract at AddressC that executes INVALID
            byte[] contractCode = Prepare.EvmCode
                .Op(Instruction.INVALID)
                .Done;

            TestState.CreateAccount(TestItem.AddressC, 1.Ether());
            TestState.InsertCode(TestItem.AddressC, contractCode, Spec);

            // Outer code calls the contract — the nested call fails with BadInstruction
            byte[] callerCode = Prepare.EvmCode
                .Call(TestItem.AddressC, 50000)
                .Done;

            ActionOnlyTracer tracer = Execute(new ActionOnlyTracer(), callerCode);

            // Should have at least one BadInstruction error from the nested call
            Assert.That(tracer.ReportedActionErrors, Has.Some.EqualTo(EvmExceptionType.BadInstruction));
        }

        /// <summary>
        /// Regression: CodeInfo.Version was hardcoded to 0 for all CodeInfo subclasses
        /// because `codeInfo is CodeInfo` always matches (EofCodeInfo inherits CodeInfo).
        /// Verify that non-zero versions propagate correctly through CodeInfo.
        /// </summary>
        [Test]
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void CodeInfo_Version_propagates_correctly(int version)
        {
            byte[] code = new byte[] { 0x00 }; // STOP
            CodeInfo codeInfo = version == 0
                ? new CodeInfo(code)
                : new TestCodeInfoWithVersion(version, code);

            Assert.That(codeInfo.Version, Is.EqualTo(version));
            Assert.That(codeInfo.CodeSpan.Length, Is.EqualTo(1));
        }

        /// <summary>
        /// A tracer that only traces actions (not instructions).
        /// Regression test verifier: ReportActionError must be called even when
        /// IsTracingInstructions is false.
        /// </summary>
        private sealed class ActionOnlyTracer : TxTracer
        {
            public override bool IsTracingReceipt => true;
            public override bool IsTracingActions => true;
            // Explicitly NOT tracing instructions — this is the key scenario
            public override bool IsTracingInstructions => false;

            public List<EvmExceptionType> ReportedActionErrors { get; } = new();

            public override void ReportActionError(EvmExceptionType exceptionType)
            {
                ReportedActionErrors.Add(exceptionType);
            }

            public override void MarkAsSuccess(Address recipient, GasConsumed gasSpent, byte[] output,
                LogEntry[] logs, Hash256? stateRoot = null)
            { }

            public override void MarkAsFailed(Address recipient, GasConsumed gasSpent, byte[] output,
                string? error, Hash256? stateRoot = null)
            { }
        }

        /// <summary>
        /// Test helper: CodeInfo subclass with a custom version, simulating EofCodeInfo
        /// without requiring a full EOF container.
        /// </summary>
        private sealed class TestCodeInfoWithVersion : CodeInfo
        {
            public TestCodeInfoWithVersion(int version, ReadOnlyMemory<byte> code)
                : base(version, code) { }
        }
    }
}
