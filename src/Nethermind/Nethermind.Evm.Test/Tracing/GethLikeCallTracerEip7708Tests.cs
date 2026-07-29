// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Call;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Precompiles;
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

    private static NativeCallTracerLogEntry ExpectedTransferLog(Address from, Address to, byte value, ulong position) => new(
        TransferLog.Sender, data: Hash256.FromBytesWithPadding([value]).BytesToArray(),
        topics: [TransferLog.TransferSignature, new(from.ToHash()), new(to.ToHash())], position
    );

    private static byte[] ForwardValueCode(Address target) =>
        Prepare.EvmCode.CallWithValue(target, 50_000, InnerValue).STOP().Done;

    private static byte[] CreateValueCode() =>
        Prepare.EvmCode.Create(Prepare.EvmCode.STOP().Done, InnerValue).STOP().Done;

    public sealed record TransferLogScenario(byte[]? RecipientCode, bool ExpectsChildFrame, string? Config = WithLog);

    [TestCaseSource(nameof(TransferLogCases))]
    public void ValueTransfer_WithLog_AddsLogsToCorrectFrames(TransferLogScenario scenario)
    {
        (Block block, Transaction tx) = PrepareTx(Activation, 200_000UL, scenario.RecipientCode, value: TopValue);
        using NativeCallTracer tracer = new(tx, GetGethTraceOptions(scenario.Config));

        _processor.Execute(tx, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);

        using GethLikeTxTrace trace = tracer.BuildResult();
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

    private static IEnumerable<TestCaseData> TransferLogCases()
    {
        yield return new TestCaseData(new TransferLogScenario(null, false))
            .SetName("top-level value transfer to EOA (simple-transfer fast path)");

        yield return new TestCaseData(new TransferLogScenario(Prepare.EvmCode.STOP().Done, false))
            .SetName("top-level value transfer to contract (EVM path)");

        yield return new TestCaseData(new TransferLogScenario(ForwardValueCode(TestItem.AddressC), true))
            .SetName("nested value transfer to EOA");

        yield return new TestCaseData(new TransferLogScenario(ForwardValueCode(Precompile), true))
            .SetName("nested value transfer to precompile");

        yield return new TestCaseData(new TransferLogScenario(CreateValueCode(), true))
            .SetName("nested value transfer to CREATE");

        yield return new TestCaseData(new TransferLogScenario(ForwardValueCode(TestItem.AddressC), false, WithLogAndOnlyTopCall))
            .SetName("nested value transfer is not hoisted into top frame under onlyTopCall");
    }
}
