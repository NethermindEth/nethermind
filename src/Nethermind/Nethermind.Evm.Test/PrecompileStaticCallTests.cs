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
    /// <summary>Largest ID output the VM keeps a scratch buffer for; anything longer is allocated per call.</summary>
    private const int RetainedScratchLimit = 1024 * 1024;

    private const int RecordSize = 96;
    private const int InputOffset = 512;

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

        byte[] expected = new byte[64];
        input.CopyTo(expected, 0);
        expected[63] = 32;

        AssertOutput(code, expected);
    }

    [Test]
    public void ReturnDataCopy_copies_an_exact_precompile_output_slice()
    {
        byte[] input = new byte[32];
        for (int i = 0; i < input.Length; i++) input[i] = (byte)(i + 1);
        byte[] code = Prepare.EvmCode
            .MSTORE(0, input)
            .STATICCALL(50_000, IdentityPrecompile.Address, 0, (UInt256)input.Length, 0, 0)
            .Op(Instruction.POP)
            .RETURNDATACOPY(64, 5, 7)
            .RETURN(64, 7)
            .Done;

        AssertOutput(code, input[5..12]);
    }

    [Test]
    public void ReturnDataCopy_after_an_identity_call_whose_output_overlaps_its_input_sees_the_input()
    {
        byte[] input = new byte[64];
        for (int i = 0; i < input.Length; i++) input[i] = (byte)(i + 1);

        byte[] code = Prepare.EvmCode
            .MSTORE(0, input[..32])
            .MSTORE(32, input[32..])
            // Output range overlaps the input range, so the copy rewrites half of [0,64) as it runs.
            .STATICCALL(50_000, IdentityPrecompile.Address, 0, 64, 32, 64)
            .Op(Instruction.POP)
            .RETURNDATACOPY(128, 0, 64)
            .RETURN(128, 64)
            .Done;

        AssertOutput(code, input);
    }

    [Test]
    public void ReturnDataCopy_after_the_frame_overwrites_the_identity_input_sees_the_input()
    {
        byte[] input = new byte[32];
        for (int i = 0; i < input.Length; i++) input[i] = (byte)(i + 1);

        byte[] code = Prepare.EvmCode
            .MSTORE(0, input)
            .STATICCALL(50_000, IdentityPrecompile.Address, 0, 32, 64, 32)
            .Op(Instruction.POP)
            .MSTORE(0, new byte[32])
            .RETURNDATACOPY(96, 0, 32)
            .RETURN(96, 32)
            .Done;

        AssertOutput(code, input);
    }

    [Test]
    public void Identity_calls_of_changing_lengths_each_return_their_own_input()
    {
        // Grow, shrink, empty, unaligned, grow back: a reused buffer must not leave a tail of a longer call behind.
        byte[] code = BuildIdentityChain([64, 32, 0, 40, 64], out byte[] expected);

        AssertOutput(code, expected);
    }

    [Test]
    public void Identity_calls_crossing_the_retained_scratch_limit_each_return_their_own_input()
    {
        // The second call fills the retained buffer exactly and the fourth exceeds it, so it is served by a
        // buffer of its own; the short calls around them must still see only their own bytes.
        byte[] code = BuildIdentityChain([32, RetainedScratchLimit, 32, RetainedScratchLimit + 32, 32], out byte[] expected);

        AssertOutput(code, expected, gasLimit: 4_000_000UL);
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

        byte[] expected = new byte[32];
        expected[31] = expectedStatus;

        AssertOutput(code, expected, fork: fork);
    }

    private void AssertOutput(byte[] code, byte[] expectedOutput, ulong gasLimit = 100_000UL, ForkActivation? fork = null)
    {
        (Block block, Transaction transaction) = PrepareTx(fork ?? Activation, gasLimit, code);
        ReceiptOnlyTracer tracer = new();
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
            Assert.That(tracer.ReturnValue, Is.EqualTo(expectedOutput));
        }
    }

    /// <summary>
    /// Chains one ID call per entry of <paramref name="lengths"/> and returns, per call, the returndata size
    /// followed by its first and last word.
    /// </summary>
    /// <remarks>Every call writes a word unique to it at both ends of its input, so returndata carrying another
    /// call's bytes — the stale tail of a longer one, or a wrongly sliced buffer — cannot go unnoticed. What lies
    /// between the two words stays zero, which keeps the bytecode the same size at any input length.</remarks>
    private static byte[] BuildIdentityChain(int[] lengths, out byte[] expected)
    {
        int longest = 0;
        foreach (int length in lengths) longest = Math.Max(longest, length);

        // Mirrors the input region of EVM memory, so each call is expected to return what the calls before it left there.
        byte[] memory = new byte[longest + 32];
        byte[] records = new byte[lengths.Length * RecordSize];
        Prepare code = Prepare.EvmCode;

        for (int step = 0; step < lengths.Length; step++)
        {
            int length = lengths[step];
            if (length > 0)
            {
                byte[] head = Word(step * 2 + 1);
                head.CopyTo(memory, 0);
                code = code.MSTORE(InputOffset, head);
            }

            if (length > 32)
            {
                byte[] tail = Word(step * 2 + 2);
                tail.CopyTo(memory, length - 32);
                code = code.MSTORE((UInt256)(InputOffset + length - 32), tail);
            }

            int record = step * RecordSize;
            code = code
                .STATICCALL(1_000_000, IdentityPrecompile.Address, InputOffset, (UInt256)length, 0, 0)
                .Op(Instruction.POP)
                .RETURNDATASIZE()
                .MSTORE((UInt256)record);
            ((UInt256)length).ToBigEndian().CopyTo(records, record);

            if (length > 0)
            {
                int headLength = Math.Min(32, length);
                code = code.RETURNDATACOPY((UInt256)(record + 32), 0, (UInt256)headLength);
                memory.AsSpan(0, headLength).CopyTo(records.AsSpan(record + 32));
            }

            if (length > 32)
            {
                code = code.RETURNDATACOPY((UInt256)(record + 64), (UInt256)(length - 32), 32);
                memory.AsSpan(length - 32, 32).CopyTo(records.AsSpan(record + 64));
            }
        }

        expected = records;
        return code.RETURN(0, (UInt256)records.Length).Done;
    }

    private static byte[] Word(int marker)
    {
        byte[] word = new byte[32];
        for (int i = 0; i < word.Length; i++) word[i] = (byte)(marker * 31 + i + 1);
        return word;
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
