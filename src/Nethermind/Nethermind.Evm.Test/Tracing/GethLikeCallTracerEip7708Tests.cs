// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Call;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;
using static Nethermind.Evm.Test.Tracing.GethLikeCallTracerTests;

namespace Nethermind.Evm.Test.Tracing;

[TestFixture]
public class GethLikeCallTracerEip7708Tests : VirtualMachineTestsBase
{
    protected override ISpecProvider SpecProvider => new TestSpecProvider(new OverridableReleaseSpec(Prague.Instance) { IsEip7708Enabled = true });

    private const byte TopValue = 1;
    private const byte InnerValue = 2;

    private static readonly Address Precompile = IdentityPrecompile.Address;

    private static class Code
    {
        public static byte[] ForwardValue(Address target) =>
            Prepare.EvmCode.CallWithValue(target, 50_000, InnerValue).STOP().Done;

        public static byte[] ForwardValueAndRevert(Address target) =>
            Prepare.EvmCode.CallWithValue(target, 50_000, InnerValue).Revert(0, 0).Done;

        public static byte[] CreateValue() =>
            Prepare.EvmCode.Create(Prepare.EvmCode.STOP().Done, InnerValue).STOP().Done;
    }

    private static NativeCallTracerLogEntry ExpectedTransferLog(Address from, Address to, byte value, ulong position) => new(
        TransferLog.Sender, data: Hash256.FromBytesWithPadding([value]).BytesToArray(),
        topics: [TransferLog.TransferSignature, new(from.ToHash()), new(to.ToHash())], position
    );

    private static NativeCallTracerLogEntry ExpectedSelfDestructLog(Address account, byte value, ulong position) => new(
        TransferLog.Sender, data: Hash256.FromBytesWithPadding([value]).BytesToArray(),
        topics: [TransferLog.SelfDestructSignature, new(account.ToHash())], position
    );

    public sealed record TransferLogScenario(byte[]? RecipientCode, bool ExpectsChildFrame, string? Config = WithLog);

    [TestCaseSource(nameof(TransferLogCases))]
    public void ValueTransfer_WithLog_AddsLogsToCorrectFrames(TransferLogScenario scenario)
    {
        using GethLikeTxTrace trace = TraceValueTransfer(scenario.RecipientCode, scenario.Config);
        NativeCallTracerCallFrame topFrame = (NativeCallTracerCallFrame)trace.CustomTracerResult!.Value!;

        NativeCallTracerLogEntry expectedTop = ExpectedTransferLog(Sender, Recipient, TopValue, 0UL);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(topFrame.Logs, Is.EqualTo([expectedTop]).UsingPropertiesComparer());
            Assert.That(topFrame.Calls, Has.Count.EqualTo(scenario.ExpectsChildFrame ? 1 : 0));
        }

        if (scenario.ExpectsChildFrame)
        {
            NativeCallTracerCallFrame childFrame = topFrame.Calls[0];
            NativeCallTracerLogEntry expectedInner = ExpectedTransferLog(Recipient, childFrame.To!, InnerValue, 0UL);
            Assert.That(childFrame.Logs, Is.EqualTo([expectedInner]).UsingPropertiesComparer());
        }
    }

    [Test(Description = "Recipient forwards value to C which reverts - C's transfer log must be cleared")]
    public void RevertInnerValueTransfer_WithLog_DoesNotLeavePhantomLog()
    {
        TestState.CreateAccount(TestItem.AddressC, 0);
        TestState.InsertCode(TestItem.AddressC, Prepare.EvmCode.Revert(0, 0).Done, Spec);

        using GethLikeTxTrace trace = TraceValueTransfer(Code.ForwardValue(TestItem.AddressC));
        NativeCallTracerCallFrame topFrame = (NativeCallTracerCallFrame)trace.CustomTracerResult!.Value!;

        NativeCallTracerCallFrame childFrame = topFrame.Calls.AssertSingle();

        NativeCallTracerLogEntry expectedTop = ExpectedTransferLog(Sender, Recipient, TopValue, 0UL);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(topFrame.Logs, Is.EqualTo([expectedTop]).UsingPropertiesComparer(), "successful parent frame must keep its log");
            Assert.That(childFrame.Error, Is.EqualTo("execution reverted"), "inner call must have reverted");
            Assert.That(childFrame.Logs, Is.Null, "phantom transfer log on a reverted frame must be cleared");
        }
    }

    [Test(Description = "C forwards value to D (succeeds, emits a transfer log) then reverts - D's log must be cleared")]
    public void RevertInnerValueTransfer_WithLog_DoesNotLeavePhantomLog_OnDescendant()
    {
        TestState.CreateAccount(TestItem.AddressC, 0);
        TestState.InsertCode(TestItem.AddressC, Code.ForwardValueAndRevert(TestItem.AddressD), Spec);
        TestState.CreateAccount(TestItem.AddressD, 0);

        using GethLikeTxTrace trace = TraceValueTransfer(Code.ForwardValue(TestItem.AddressC));
        NativeCallTracerCallFrame topFrame = (NativeCallTracerCallFrame)trace.CustomTracerResult!.Value!;

        NativeCallTracerCallFrame childFrame = topFrame.Calls.AssertSingle();
        NativeCallTracerCallFrame grandchildFrame = childFrame.Calls.AssertSingle();

        NativeCallTracerLogEntry expectedTop = ExpectedTransferLog(Sender, Recipient, TopValue, 0UL);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(topFrame.Logs, Is.EqualTo([expectedTop]).UsingPropertiesComparer(), "successful top frame must keep its log");
            Assert.That(childFrame.Error, Is.EqualTo("execution reverted"), "inner call must have reverted");
            Assert.That(childFrame.Logs, Is.Null, "reverted frame's transfer log must be cleared");
            Assert.That(grandchildFrame.Error, Is.Null, "descendant call itself succeeded");
            Assert.That(grandchildFrame.Logs, Is.Null, "log on a successful frame under a reverted ancestor must also be cleared");
        }
    }

    private GethLikeTxTrace TraceValueTransfer(byte[]? recipientCode, string? config = WithLog)
    {
        (Block block, Transaction tx) = PrepareTx(Activation, 200_000UL, recipientCode, value: TopValue);
        using NativeCallTracer tracer = new(tx, Amsterdam.Instance, GetGethTraceOptions(config));
        _processor.Execute(tx, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);
        return tracer.BuildResult();
    }

    [Test(Description = "Destroy-list finalization log must reach log tracers, not just receipts")]
    public void FinalizationSelfDestructLog_WithLog_AppearsInTopFrame()
    {
        const byte initBalance = 5;
        const byte fundedAfter = 7;
        Address inheritor = TestItem.AddressC;

        byte[] contractACode = Prepare.EvmCode
            .CALLVALUE()
            .Op(Instruction.ISZERO)
            .PushData(6)
            .JUMPI()
            .STOP()
            .JUMPDEST()
            .SELFDESTRUCT(inheritor)
            .Done;
        byte[] initCodeA = Prepare.EvmCode
            .ForInitOf(contractACode)
            .Done;

        Address contractA = ContractAddress.From(Recipient, 0);

        byte[] factoryCode = Prepare.EvmCode
            .Create(initCodeA, initBalance)
            .Call(contractA, 100_000)
            .CallWithValue(contractA, 100_000, fundedAfter)
            .STOP()
            .Done;

        (Block block, Transaction tx) = PrepareTx(Activation, 2_000_000UL, factoryCode, value: 0);
        using NativeCallTracer tracer = new(tx, Amsterdam.Instance, GetGethTraceOptions(WithLog));
        _processor.Execute(tx, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);
        using GethLikeTxTrace trace = tracer.BuildResult();
        NativeCallTracerCallFrame topFrame = (NativeCallTracerCallFrame)trace.CustomTracerResult!.Value!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(topFrame.Calls, Has.Count.EqualTo(3), "factory must create then call twice");
            Assert.That(topFrame.Calls[0].Logs, Is.EqualTo([ExpectedTransferLog(Recipient, contractA, initBalance, 0UL)]).UsingPropertiesComparer(), "CREATE endowment log on create frame");
            Assert.That(topFrame.Calls[1].Logs, Is.EqualTo([ExpectedTransferLog(contractA, inheritor, initBalance, 1UL)]).UsingPropertiesComparer(), "SELFDESTRUCT transfer log on call frame (position follows the SELFDESTRUCT child frame recorded first; geth records AddLog before the child frame, so cross-client position differs here)");
            Assert.That(topFrame.Calls[2].Logs, Is.EqualTo([ExpectedTransferLog(Recipient, contractA, fundedAfter, 0UL)]).UsingPropertiesComparer(), "post-destruct funding log on call frame");
            Assert.That(topFrame.Logs, Is.EqualTo([ExpectedSelfDestructLog(contractA, fundedAfter, 3UL)]).UsingPropertiesComparer(), "finalization log must be reported to log tracers on the top frame");
        }
    }

    private static IEnumerable<TestCaseData> TransferLogCases()
    {
        yield return new TestCaseData(new TransferLogScenario(null, false))
            .SetName("top-level value transfer to EOA (simple-transfer fast path)");

        yield return new TestCaseData(new TransferLogScenario(Prepare.EvmCode.STOP().Done, false))
            .SetName("top-level value transfer to contract (EVM path)");

        yield return new TestCaseData(new TransferLogScenario(Code.ForwardValue(TestItem.AddressC), true))
            .SetName("nested value transfer to EOA");

        yield return new TestCaseData(new TransferLogScenario(Code.ForwardValue(Precompile), true))
            .SetName("nested value transfer to precompile");

        yield return new TestCaseData(new TransferLogScenario(Code.CreateValue(), true))
            .SetName("nested value transfer to CREATE");

        yield return new TestCaseData(new TransferLogScenario(Code.ForwardValue(TestItem.AddressC), false, WithLogAndOnlyTopCall))
            .SetName("nested value transfer is not hoisted into top frame under onlyTopCall");
    }
}

[TestFixture]
public class GethLikeCallTracerEip7708DeferredTests : VirtualMachineTestsBase
{
    // Deferred finalization path: EIP-7708 + EIP-8037 without EIP-8246, so destroyed accounts
    // with residual balance emit Burn logs after PayFees (rather than inline SelfDestruct logs).
    protected override ISpecProvider SpecProvider => new TestSpecProvider(new OverridableReleaseSpec(Prague.Instance) { IsEip7708Enabled = true, IsEip8037Enabled = true });

    private static NativeCallTracerLogEntry ExpectedBurnLog(Address account, byte value, ulong position) => new(
        TransferLog.Sender, data: Hash256.FromBytesWithPadding([value]).BytesToArray(),
        topics: [TransferLog.BurnSignature, new(account.ToHash())], position
    );

    [Test(Description = "Deferred Burn finalization log must reach log tracers, not just receipts")]
    public void FinalizationBurnLog_WithLog_AppearsInTopFrame()
    {
        const byte initBalance = 5;
        const byte fundedAfter = 7;
        Address inheritor = TestItem.AddressC;

        byte[] contractACode = Prepare.EvmCode
            .CALLVALUE()
            .Op(Instruction.ISZERO)
            .PushData(6)
            .JUMPI()
            .STOP()
            .JUMPDEST()
            .SELFDESTRUCT(inheritor)
            .Done;
        byte[] initCodeA = Prepare.EvmCode
            .ForInitOf(contractACode)
            .Done;

        Address contractA = ContractAddress.From(Recipient, 0);

        byte[] factoryCode = Prepare.EvmCode
            .Create(initCodeA, initBalance)
            .Call(contractA, 500_000)
            .CallWithValue(contractA, 500_000, fundedAfter)
            .STOP()
            .Done;

        (Block block, Transaction tx) = PrepareTx(Activation, 5_000_000UL, factoryCode, value: 0);
        IReleaseSpec spec = SpecProvider.GetSpec(block.Header);
        using NativeCallTracer tracer = new(tx, spec, GethLikeCallTracerTests.GetGethTraceOptions(GethLikeCallTracerTests.WithLog));
        _processor.Execute(tx, new BlockExecutionContext(block.Header, spec), tracer);
        using GethLikeTxTrace trace = tracer.BuildResult();
        NativeCallTracerCallFrame topFrame = (NativeCallTracerCallFrame)trace.CustomTracerResult!.Value!;

        Assert.That(topFrame.Logs, Is.EqualTo([ExpectedBurnLog(contractA, fundedAfter, 3UL)]).UsingPropertiesComparer(), "deferred Burn log must be reported to log tracers on the top frame");
    }
}
