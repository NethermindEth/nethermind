// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.StateGas;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test.Tracing;

// End-to-end: runs a real Amsterdam transaction through TransactionProcessor so GasConsumed's field
// selection and GasRefund plumbing are exercised, not just the struct-to-JSON mapping.
[TestFixture]
public class NativeStateGasTracerE2ETests : VirtualMachineTestsBase
{
    protected override ulong BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.AmsterdamBlockTimestamp;

    [Test]
    public void State_creating_sstore_with_clear_records_state_gas_and_refund()
    {
        // slot 0: 0 -> 1 kept (fresh => state gas); slot 1: 0 -> 1 -> 0 (reset to original => EIP-3529 refund).
        byte[] code = Prepare.EvmCode
            .PushData(1).PushData(0).Op(Instruction.SSTORE)
            .PushData(1).PushData(1).Op(Instruction.SSTORE)
            .PushData(0).PushData(1).Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Done;

        (Block block, Transaction tx) = PrepareTx(Activation, 1_000_000, code, value: 0);
        IReleaseSpec spec = SpecProvider.GetSpec(Activation);

        using NativeStateGasTracer tracer = new(tx, spec, GethTraceOptions.Default);
        _processor.Execute(tx, new BlockExecutionContext(block.Header, spec), tracer);
        using GethLikeTxTrace trace = tracer.BuildResult();

        StateGasTrace result = (StateGasTrace)trace.CustomTracerResult!.Value;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StateGasUsed, Is.GreaterThan(0), "a fresh-slot SSTORE must record state gas");
            Assert.That(result.GasRefund, Is.GreaterThan(0), "resetting a slot to its original value must record an EIP-3529 refund");
            Assert.That(result.RegularGasUsed + result.StateGasUsed, Is.EqualTo(result.GasUsed + result.GasRefund),
                "regularGasUsed + stateGasUsed == gasUsed + gasRefund");
        }
    }
}
