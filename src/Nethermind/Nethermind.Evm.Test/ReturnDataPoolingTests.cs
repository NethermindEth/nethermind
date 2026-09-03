// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// Nested-call outputs are copied into pooled buffers on the cancelable (RPC) path; the parent must still see
/// exactly the bytes each call produced through RETURNDATACOPY, across a success, a revert and a replacement.
/// </summary>
[TestFixture]
public class ReturnDataPoolingTests : VirtualMachineTestsBase
{
    private const int ReturnedLength = 1024;
    private const int RevertedLength = 600;

    [Test]
    public void Pooled_nested_outputs_match_the_unpooled_execution()
    {
        byte[] code = PrepareContracts();

        byte[] pooled = Run(code, cancelable: true);
        byte[] unpooled = Run(code, cancelable: false);

        Assert.That(pooled, Is.EqualTo(unpooled));
        Assert.That(pooled, Has.Length.EqualTo(ReturnedLength + RevertedLength));
        Assert.That(pooled.AsSpan(0, 32).ToArray(), Is.EqualTo(Word(0xAA)));
        Assert.That(pooled.AsSpan(ReturnedLength - 32, 32).ToArray(), Is.EqualTo(Word(0xAA)));
        Assert.That(pooled.AsSpan(32, ReturnedLength - 64).ToArray(), Is.All.Zero);
        Assert.That(pooled.AsSpan(ReturnedLength, 32).ToArray(), Is.EqualTo(Word(0xBB)));
        Assert.That(pooled.AsSpan(ReturnedLength + RevertedLength - 32, 32).ToArray(), Is.EqualTo(Word(0xBB)));
    }

    [Test]
    public void Repeated_executions_on_the_same_machine_stay_consistent()
    {
        byte[] code = PrepareContracts();
        byte[] first = Run(code, cancelable: true);

        for (int i = 0; i < 8; i++)
        {
            Assert.That(Run(code, cancelable: true), Is.EqualTo(first));
        }
    }

    private static byte[] Word(byte value) => Enumerable.Repeat(value, 32).ToArray();

    private byte[] PrepareContracts()
    {
        Address returning = TestItem.AddressC;
        Address reverting = TestItem.AddressD;
        TestState.CreateAccount(returning, UInt256.Zero);
        TestState.CreateAccount(reverting, UInt256.Zero);
        TestState.InsertCode(returning, Prepare.EvmCode
            .MSTORE(0, Word(0xAA))
            .MSTORE(ReturnedLength - 32, Word(0xAA))
            .RETURN(0, ReturnedLength)
            .Done, SpecProvider.GenesisSpec);
        TestState.InsertCode(reverting, Prepare.EvmCode
            .MSTORE(0, Word(0xBB))
            .MSTORE(RevertedLength - 32, Word(0xBB))
            .REVERT(0, RevertedLength)
            .Done, SpecProvider.GenesisSpec);

        return Prepare.EvmCode
            .CALL(30_000, returning, 0, 0, 0, 0, 0)
            .Op(Instruction.POP)
            .RETURNDATACOPY(0, 0, ReturnedLength)
            .CALL(30_000, reverting, 0, 0, 0, 0, 0)
            .Op(Instruction.POP)
            .RETURNDATACOPY(ReturnedLength, 0, RevertedLength)
            .RETURN(0, ReturnedLength + RevertedLength)
            .Done;
    }

    private byte[] Run(byte[] code, bool cancelable)
    {
        OutputTracer output = new();
        ITxTracer tracer = cancelable ? new CancellationTxTracer(output) : output;
        Execute(tracer, code);
        Assert.That(output.Error, Is.Null);
        return output.Output!;
    }

    private sealed class OutputTracer : TxTracer
    {
        public byte[]? Output { get; private set; }
        public string? Error { get; private set; }
        public override bool IsTracingReceipt => true;

        public override void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null) =>
            Output = output;

        public override void MarkAsFailed(Address recipient, in GasConsumed gasSpent, byte[] output, string? error, Hash256? stateRoot = null)
        {
            Output = output;
            Error = error ?? "failed";
        }
    }
}
