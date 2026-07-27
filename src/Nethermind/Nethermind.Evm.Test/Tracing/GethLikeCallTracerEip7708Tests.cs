// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Json;
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

namespace Nethermind.Evm.Test.Tracing;

[TestFixture]
public class GethLikeCallTracerEip7708Tests : VirtualMachineTestsBase
{
    protected override ISpecProvider SpecProvider => new TestSpecProvider(new OverridableReleaseSpec(Prague.Instance) { IsEip7708Enabled = true });

    private const string WithLog = """{"withLog":true}""";

    private const byte TopValue = 1;
    private const byte InnerValue = 2;

    private static readonly Address Precompile = IdentityPrecompile.Address;

    private static GethTraceOptions GetGethTraceOptions(string? config) => GethTraceOptions.Default with
    {
        Tracer = NativeCallTracer.CallTracer,
        TracerConfig = config is not null ? JsonSerializer.Deserialize<JsonElement>(config) : null
    };

    private static NativeCallTracerLogEntry ExpectedTransferLog(Address from, Address to, byte value, ulong position) => new(
        TransferLog.Sender, data: Hash256.FromBytesWithPadding([value]).BytesToArray(),
        topics: [TransferLog.TransferSignature, new(from.ToHash()), new(to.ToHash())], position
    );

    private static byte[] ForwardValueCode(Address target) =>
        Prepare.EvmCode.CallWithValue(target, 50_000, InnerValue).STOP().Done;

    public sealed record TransferLogScenario(byte[]? RecipientCode, Address? InnerTarget);

    [TestCaseSource(nameof(TransferLogCases))]
    public void ValueTransfer_WithLog_AttachesLogToCorrectFrame(TransferLogScenario scenario)
    {
        (Block block, Transaction tx) = PrepareTx(Activation, 200_000UL, scenario.RecipientCode, value: TopValue);
        using NativeCallTracer tracer = new(tx, GetGethTraceOptions(WithLog));

        _processor.Execute(tx, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);

        using GethLikeTxTrace trace = tracer.BuildResult();
        NativeCallTracerCallFrame topFrame = (NativeCallTracerCallFrame)trace.CustomTracerResult!.Value!;

        NativeCallTracerLogEntry expectedTop = ExpectedTransferLog(Sender, Recipient, TopValue, 0UL);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(topFrame.Logs, Is.EqualTo([expectedTop]).UsingPropertiesComparer());
            Assert.That(topFrame.Calls, Has.Count.EqualTo(scenario.InnerTarget is null ? 0 : 1));
        }

        if (scenario.InnerTarget is not null)
        {
            NativeCallTracerLogEntry expectedInner = ExpectedTransferLog(Recipient, scenario.InnerTarget, InnerValue, 0UL);
            Assert.That(topFrame.Calls[0].Logs, Is.EqualTo([expectedInner]).UsingPropertiesComparer());
        }
    }

    private static IEnumerable<TestCaseData> TransferLogCases()
    {
        yield return new TestCaseData(new TransferLogScenario(null, null))
            .SetName("top-level value transfer to EOA (simple-transfer fast path)");

        yield return new TestCaseData(new TransferLogScenario(Prepare.EvmCode.STOP().Done, null))
            .SetName("top-level value transfer to contract (EVM path)");

        yield return new TestCaseData(new TransferLogScenario(ForwardValueCode(TestItem.AddressC), TestItem.AddressC))
            .SetName("nested value transfer to EOA");

        yield return new TestCaseData(new TransferLogScenario(ForwardValueCode(Precompile), Precompile))
            .SetName("nested value transfer to precompile");
    }
}
