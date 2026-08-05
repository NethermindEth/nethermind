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
        using NativeCallTracer tracer = new(tx, GetGethTraceOptions(config));
        _processor.Execute(tx, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);
        return tracer.BuildResult();
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
