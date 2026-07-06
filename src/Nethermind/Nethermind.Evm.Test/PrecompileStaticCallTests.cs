// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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

        ReceiptTracer tracer = Execute(new ReceiptTracer(traceActions: false), code);

        byte[] expected = new byte[64];
        input.CopyTo(expected, 0);
        expected[63] = 32;

        Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
        Assert.That(tracer.ReturnValue, Is.EqualTo(expected));
    }

    // The direct precompile fast path (no action tracing) must reproduce the full call-frame path
    // (action tracing forces it) bit for bit on both failure branches of ExecuteStaticPrecompileCallDirectly:
    //   - OutOfGas: forwarded gas below the precompile base cost (UpdateGas fails).
    //   - PrecompileFailure: precompile returns a non-OOG failure (Blake2F rejects a wrong-length input).
    [TestCase(FailureBranch.OutOfGas)]
    [TestCase(FailureBranch.PrecompileFailure)]
    public void Fast_path_precompile_failure_matches_full_path(FailureBranch branch)
    {
        (Address precompile, UInt256 staticCallGas, UInt256 inputLength) = branch switch
        {
            // ECRECOVER base cost is 3000; forwarding 100 gas out-of-gases inside the child frame.
            FailureBranch.OutOfGas => (ECRecoverPrecompile.Address, (UInt256)100, UInt256.Zero),
            // BLAKE2F rejects any input whose length is not 213 with a non-OOG failure at zero data cost.
            FailureBranch.PrecompileFailure => (Blake2FPrecompile.Address, (UInt256)50_000, (UInt256)32),
            _ => throw new ArgumentOutOfRangeException(nameof(branch))
        };

        byte[] code = Prepare.EvmCode
            .STATICCALL(staticCallGas, precompile, 0, inputLength, 0, 0)
            .MSTORE(0)
            .RETURN(0, 32)
            .Done;

        // Istanbul: STATICCALL and BLAKE2F both exist; EIP-8037 state gas is inactive.
        ForkActivation activation = new(MainnetSpecProvider.IstanbulBlockNumber);

        ReceiptTracer fastPath = Execute(new ReceiptTracer(traceActions: false), code, activation);
        ReceiptTracer fullPath = Execute(new ReceiptTracer(traceActions: true), code, activation);

        Assert.That(fastPath.StatusCode, Is.EqualTo(fullPath.StatusCode), "status code");
        Assert.That(fastPath.ReturnValue, Is.EqualTo(fullPath.ReturnValue), "return value");
        Assert.That(fastPath.GasSpent, Is.EqualTo(fullPath.GasSpent), "gas spent");

        // The failed sub-call pushes 0; the outer frame stores and returns that 0 and succeeds.
        Assert.That(fastPath.StatusCode, Is.EqualTo(StatusCode.Success));
        Assert.That(fastPath.ReturnValue, Is.EqualTo(new byte[32]));
    }

    public enum FailureBranch
    {
        OutOfGas,
        PrecompileFailure
    }

    private sealed class ReceiptTracer(bool traceActions) : TxTracer
    {
        public override bool IsTracingReceipt => true;

        // Toggling only action tracing selects the code path: true forces the full call frame,
        // false enables the direct precompile fast path. Gas accounting is identical either way.
        public override bool IsTracingActions => traceActions;

        public byte[] ReturnValue { get; private set; } = [];

        public byte StatusCode { get; private set; }

        public long GasSpent { get; private set; }

        public override void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
        {
            ReturnValue = output;
            StatusCode = Evm.StatusCode.Success;
            GasSpent = gasSpent.SpentGas;
        }

        public override void MarkAsFailed(Address recipient, in GasConsumed gasSpent, byte[] output, string? error, Hash256? stateRoot = null)
        {
            ReturnValue = output ?? [];
            StatusCode = Evm.StatusCode.Failure;
            GasSpent = gasSpent.SpentGas;
        }
    }
}
