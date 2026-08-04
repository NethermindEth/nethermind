// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.StateGas;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Serialization.Json;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Evm.Test.Tracing;

public class NativeStateGasTracerTests
{
    private static string Trace(bool eip8037Enabled, in GasConsumed gasSpent)
    {
        IReleaseSpec spec = Substitute.For<IReleaseSpec>();
        spec.IsEip8037Enabled.Returns(eip8037Enabled);

        Transaction tx = Build.A.Transaction.TestObject;
        using NativeStateGasTracer tracer = new(tx, spec, GethTraceOptions.Default with { Tracer = NativeStateGasTracer.StateGasTracer });
        tracer.MarkAsSuccess(TestItem.AddressA, in gasSpent, [], []);

        using GethLikeTxTrace trace = tracer.BuildResult();

        // The enclosing custom-trace converter serializes numbers as raw decimals; reproduce that ambient
        // state so this test actually exercises StateGasTraceConverter's hex-quantity override.
        NumberConversion previous = ForcedNumberConversion.Value;
        ForcedNumberConversion.Value = NumberConversion.Raw;
        try
        {
            return JsonSerializer.Serialize(trace.CustomTracerResult?.Value, EthereumJsonSerializer.JsonOptions);
        }
        finally
        {
            ForcedNumberConversion.Value = previous;
        }
    }

    [Test]
    public void Post_fork_reports_two_dimensional_gas_as_hex_quantities()
    {
        // regularGasUsed + stateGasUsed == gasUsed + gasRefund (25000 + 5000 == 21000 + 9000)
        GasConsumed gasSpent = new(SpentGas: 21000, OperationGas: 21000, BlockGas: 25000, BlockStateGas: 5000, MaxUsedGas: 30000, GasRefund: 9000);

        string trace = Trace(eip8037Enabled: true, in gasSpent);

        Assert.That(trace, Is.EqualTo("""{"gasUsed":"0x5208","regularGasUsed":"0x61a8","stateGasUsed":"0x1388","gasRefund":"0x2328"}"""));
    }

    [Test]
    public void Pre_fork_reports_full_pre_refund_gas_and_zero_state_gas()
    {
        GasConsumed gasSpent = new(SpentGas: 21000, OperationGas: 21000, BlockGas: 0, BlockStateGas: 0, MaxUsedGas: 25000, GasRefund: 4000);

        string trace = Trace(eip8037Enabled: false, in gasSpent);

        Assert.That(trace, Is.EqualTo("""{"gasUsed":"0x5208","regularGasUsed":"0x61a8","stateGasUsed":"0x0","gasRefund":"0xfa0"}"""));
    }
}
