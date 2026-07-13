// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class PrecompileStaticCallTests : VirtualMachineTestsBase
{
    [Test]
    public void Staticcall_to_precompile_without_tracing_copies_output_and_sets_returndata()
    {
        byte[] input = new byte[32];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = (byte)(i + 1);
        }

        byte[] code = Prepare.EvmCode
            .MSTORE(0, input)
            .STATICCALL(50_000, IdentityPrecompile.Address, 0, (UInt256)input.Length, 64, (UInt256)input.Length)
            .RETURNDATASIZE()
            .MSTORE(96)
            .RETURN(64, 64)
            .Done;

        ReceiptOnlyTracer tracer = Execute(new ReceiptOnlyTracer(), code);

        byte[] expected = new byte[64];
        input.CopyTo(expected, 0);
        expected[63] = 32;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
            Assert.That(tracer.ReturnValue, Is.EqualTo(expected));
        }
    }

    [Test]
    public void Staticcall_to_precompile_below_base_gas_cost_returns_zero()
        => AssertStaticCallStatus(ECRecoverPrecompile.Address, inputLength: 128, gasForwarded: 100, expectedStatus: 0);

    [Test]
    public void Staticcall_to_failing_precompile_returns_zero()
        => AssertStaticCallStatus(Blake2FPrecompile.Address, inputLength: 32, gasForwarded: 50_000, expectedStatus: 0, MainnetSpecProvider.CancunActivation);

    private void AssertStaticCallStatus(Address precompile, int inputLength, UInt256 gasForwarded, byte expectedStatus, ForkActivation? fork = null)
    {
        byte[] code = Prepare.EvmCode
            .STATICCALL(gasForwarded, precompile, 0, (UInt256)inputLength, 0, 0)
            .MSTORE(0)
            .RETURN(0, 32)
            .Done;

        ReceiptOnlyTracer tracer = Execute(new ReceiptOnlyTracer(), code, fork);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
            Assert.That(tracer.ReturnValue[31], Is.EqualTo(expectedStatus));
        }
    }

    private sealed class ReceiptOnlyTracer : TxTracer
    {
        public override bool IsTracingReceipt => true;

        public byte[] ReturnValue { get; private set; } = [];

        public byte StatusCode { get; private set; }

        public override void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
        {
            ReturnValue = output;
            StatusCode = Evm.StatusCode.Success;
        }

        public override void MarkAsFailed(Address recipient, in GasConsumed gasSpent, byte[] output, string? error, Hash256? stateRoot = null)
        {
            ReturnValue = output ?? [];
            StatusCode = Evm.StatusCode.Failure;
        }
    }
}
