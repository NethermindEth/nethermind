// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>A registered storage-read function served without a frame answers, charges and changes state as its frame does.</summary>
public class StorageReadCallsTests : VirtualMachineTestsBase
{
    private const uint Selector = 0x1e2eaeaf;

    // Returns its own storage at the word after the selector. Its frame uses 21 gas besides the SLOAD: PUSH1 4 and
    // CALLDATALOAD (6), PUSH1 0 and MSTORE with a one-word expansion (9), and the two pushes before RETURN (6).
    private static readonly byte[] ReaderCode = Prepare.EvmCode
        .PushData(4)
        .Op(Instruction.CALLDATALOAD)
        .Op(Instruction.SLOAD)
        .DataOnStackToMemory(0)
        .Return(32, 0)
        .Done;

    private const ulong ReaderBaseGas = 21;

    protected override ulong BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.CancunBlockTimestamp;

    [TearDown]
    public void ClearRegistrations() => StorageReadCalls.Clear();

    [TestCase(false)]
    [TestCase(true)]
    public void Registered_read_answers_and_charges_as_its_frame(bool unrecorded)
    {
        TestState.CreateAccount(TestItem.AddressC, 1.Ether);
        TestState.InsertCode(TestItem.AddressC, ReaderCode, Spec);
        TestState.Set(new StorageCell(TestItem.AddressC, 7), new UInt256(0x1234));
        TestState.Commit(Spec);

        // Reads slot 7 twice, cold then warm, and an unset slot once, returning the three words.
        byte[] code = ReadInto(ReadInto(ReadInto(Prepare.EvmCode, 7, 0x100), 7, 0x120), 9, 0x140)
            .Return(96, 0x100)
            .Done;

        OutputTracer framed = Execute(new OutputTracer(), code);
        StorageReadCalls.Register(new StorageReadCall(ValueKeccak.Compute(ReaderCode), Selector, ReaderBaseGas, unrecorded));
        OutputTracer served = Execute(new OutputTracer(), code);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(framed.Success, Is.True);
            Assert.That(served.Success, Is.True);
            Assert.That(served.Output, Is.EqualTo(framed.Output));
            Assert.That(served.GasSpent, Is.EqualTo(framed.GasSpent));
            Assert.That(new UInt256(served.Output.AsSpan(0, 32), isBigEndian: true), Is.EqualTo(new UInt256(0x1234)));
        }
    }

    [Test]
    public void Registered_read_is_served_without_its_frame()
    {
        TestState.CreateAccount(TestItem.AddressC, 1.Ether);
        TestState.InsertCode(TestItem.AddressC, ReaderCode, Spec);
        TestState.Commit(Spec);
        byte[] code = ReadInto(ReadInto(Prepare.EvmCode, 7, 0x100), 8, 0x120).Return(64, 0x100).Done;

        OutputTracer framed = Execute(new OutputTracer(), code);
        // A base gas one below the frame's: served, each of the two calls charges one gas less than its frame would.
        StorageReadCalls.Register(new StorageReadCall(ValueKeccak.Compute(ReaderCode), Selector, ReaderBaseGas - 1));
        OutputTracer served = Execute(new OutputTracer(), code);

        Assert.That(served.GasSpent, Is.EqualTo(framed.GasSpent - 2));
    }

    [Test]
    public void Unregistered_code_and_other_selectors_run_their_frame()
    {
        TestState.CreateAccount(TestItem.AddressC, 1.Ether);
        TestState.InsertCode(TestItem.AddressC, ReaderCode, Spec);
        TestState.Set(new StorageCell(TestItem.AddressC, 7), new UInt256(0x1234));
        TestState.Commit(Spec);
        byte[] code = ReadInto(Prepare.EvmCode, 7, 0x100).Return(32, 0x100).Done;

        OutputTracer framed = Execute(new OutputTracer(), code);
        // Registered with a base gas off by one: served, the call would charge one gas less than its frame.
        StorageReadCalls.Register(new StorageReadCall(ValueKeccak.Compute(ReaderCode), Selector + 1, ReaderBaseGas - 1));
        StorageReadCalls.Register(new StorageReadCall(ValueKeccak.Compute([0x00]), Selector, ReaderBaseGas - 1));
        OutputTracer other = Execute(new OutputTracer(), code);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(other.Output, Is.EqualTo(framed.Output));
            Assert.That(other.GasSpent, Is.EqualTo(framed.GasSpent));
        }
    }

    /// <summary>STATICCALLs the reader with the selector and <paramref name="slot"/>, copying its 32-byte output to <paramref name="outputOffset"/>.</summary>
    private static Prepare ReadInto(Prepare code, int slot, int outputOffset)
    {
        byte[] input = new byte[36];
        Bytes.FromHexString(Selector.ToString("x8")).CopyTo(input, 0);
        new UInt256((ulong)slot).ToBigEndian(input.AsSpan(4, 32));
        return code
            .StoreDataInMemory(0, input)
            .PushData(32)
            .PushData(outputOffset)
            .PushData(36)
            .PushData(0)
            .PushData(TestItem.AddressC)
            .PushData(50_000)
            .Op(Instruction.STATICCALL)
            .Op(Instruction.POP);
    }

    /// <summary>Traces the receipt only, which leaves the frameless path open: any tracing of actions or opcodes closes it.</summary>
    private sealed class OutputTracer : TxTracer
    {
        public override bool IsTracingReceipt => true;
        public bool Success { get; private set; }
        public byte[] Output { get; private set; } = [];
        public ulong GasSpent { get; private set; }

        public override void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
        {
            Success = true;
            Output = output;
            GasSpent = gasSpent.SpentGas;
        }

        public override void MarkAsFailed(Address recipient, in GasConsumed gasSpent, byte[] output, string? error, Hash256? stateRoot = null)
        {
            Output = output ?? [];
            GasSpent = gasSpent.SpentGas;
        }
    }
}
