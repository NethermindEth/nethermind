// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Call;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
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

    private static GethTraceOptions GetGethTraceOptions(string? config) => GethTraceOptions.Default with
    {
        Tracer = NativeCallTracer.CallTracer,
        TracerConfig = config is not null ? JsonSerializer.Deserialize<JsonElement>(config) : null
    };

    [TestCase(true, TestName = "value transfer to contract (EVM path)")]
    [TestCase(false, TestName = "value transfer to EOA (simple-transfer path)")]
    public void TopLevelValueTransfer_WithLog_IncludesTransferLogToTopFrame(bool recipientHasCode)
    {
        byte[]? recipientCode = recipientHasCode ? Prepare.EvmCode.Op(Instruction.STOP).Done : null;

        const byte value = 1;
        (Block block, Transaction tx) = PrepareTx(Activation, 100_000UL, recipientCode, value: value);
        using NativeCallTracer tracer = new(tx, GetGethTraceOptions("{\"withLog\":true}"));

        _processor.Execute(tx, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);

        using GethLikeTxTrace trace = tracer.BuildResult();
        NativeCallTracerCallFrame? frame = trace.CustomTracerResult?.Value as NativeCallTracerCallFrame;
        Assert.That(frame?.Logs?.Count, Is.EqualTo(1));

        NativeCallTracerLogEntry log = frame.Logs[0];
        NativeCallTracerLogEntry expected = new(TransferLog.Sender, Hash256.FromBytesWithPadding([value]).BytesToArray(),
            [TransferLog.TransferSignature, new(Sender.ToHash()), new(Recipient.ToHash())], 0UL);
        Assert.That(log, Is.EqualTo(expected).UsingPropertiesComparer());
    }
}
