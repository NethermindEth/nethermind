// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>Random programs of the stack opcodes run as whole transactions, through the dispatch loop that enters each table.</summary>
/// <remarks>
/// <see cref="HostMemoryFastPathTests"/> enters the chain the way the dispatch loop does; this runs the loop itself, so the
/// untraced drivers' write-back of the gas and stack head the chain carries - on a halt, a fault, a padding STOP and a
/// cancellation poll - is covered too. Each program starts from a stack of random depth, up to the limit, and ends by
/// returning its memory with the top of its stack stored at zero. A receipt-only tracer takes the untraced table, a
/// cancellation tracer around it the cancelable one, and an instruction tracer the traced one; status, gas spent, error
/// and output must agree. Each program that halts also runs on exactly the gas it used and on one less. With
/// <c>HOST_DISPATCH_DUMP</c> set, the outcomes are also written to that file for comparison with another build.
/// </remarks>
public class HostDispatchEndToEndTests : VirtualMachineTestsBase
{
    private const int ProgramsPerSeed = 600;

    protected override ForkActivation Activation => MainnetSpecProvider.OsakaActivation;

    private static readonly object DumpLock = new();

    private static IEnumerable<TestCaseData> Seeds()
    {
        for (int seed = 0; seed < 8; seed++)
            yield return new TestCaseData(seed).SetName($"{{m}}({seed})");
    }

    [TestCaseSource(nameof(Seeds))]
    public void Random_stack_programs_agree_across_tables_end_to_end(int seed)
    {
        List<string> mismatches = [];
        List<string> dump = [];
        for (int i = 0; i < ProgramsPerSeed && mismatches.Count < 5; i++)
        {
            Random random = new(seed * 1_000_037 + i);
            int depth = random.Next(6) switch
            {
                0 => 0,
                1 => random.Next(1, 4),
                2 => random.Next(4, 40),
                3 => random.Next(EvmStack.MaxStackSize - 24, EvmStack.MaxStackSize),
                4 => EvmStack.MaxStackSize - 2,
                _ => random.Next(16, 40),
            };

            List<byte> code = [];
            for (int d = 0; d < depth; d++) code.Add((byte)Instruction.PUSH0);
            code.AddRange(HostMemoryFastPathTests.GenerateStackProgram(random, depth));
            code.AddRange([(byte)Instruction.PUSH0, (byte)Instruction.MSTORE, (byte)Instruction.MSIZE, (byte)Instruction.PUSH0, (byte)Instruction.RETURN]);
            byte[] program = code.ToArray();

            ulong budget = random.Next(5) switch
            {
                0 => (ulong)random.Next(0, 16),
                1 => (ulong)random.Next(0, 80),
                2 => (ulong)random.Next(0, 600) + 2UL * (ulong)depth,
                3 => 20_000,
                _ => 60_000,
            };

            ulong gasLimit = GasCostOf.Transaction + budget;
            for (int pass = 0; pass < 3; pass++)
            {
                Outcome traced = Run(program, gasLimit, Table.Traced);
                Outcome untraced = Run(program, gasLimit, Table.NoTrace);
                Outcome cancelable = Run(program, gasLimit, Table.NoTraceCancelable);
                if (untraced != traced || cancelable != traced)
                    mismatches.Add($"seed {seed} program {i} pass {pass} depth {depth} gas {gasLimit} code {Convert.ToHexString(program)}\n untraced   {untraced}\n cancelable {cancelable}\n traced     {traced}");
                dump.Add($"e2e {seed} {i} {pass} {Convert.ToHexString(program)} g{gasLimit} | {untraced} | {cancelable}");

                // Gas boundaries: exactly what the program used, then one short of it.
                if (traced.Status != StatusCode.Success || traced.GasSpent <= GasCostOf.Transaction) break;
                gasLimit = traced.GasSpent - (ulong)pass;
            }
        }

        string? path = Environment.GetEnvironmentVariable("HOST_DISPATCH_DUMP");
        if (!string.IsNullOrEmpty(path))
        {
            lock (DumpLock)
                File.AppendAllLines(path, dump);
        }

        Assert.That(mismatches, Is.Empty);
    }

    private enum Table { NoTrace, NoTraceCancelable, Traced }

    private readonly record struct Outcome(byte Status, ulong GasSpent, string? Error, string Output);

    private Outcome Run(byte[] code, ulong gasLimit, Table table)
    {
        (Block block, Transaction transaction) = PrepareTx(Activation, gasLimit, code);
        ReceiptTracer receipt = table == Table.Traced ? new InstructionTracer() : new ReceiptTracer();
        ITxTracer tracer = table == Table.NoTraceCancelable ? new CancellationTxTracer(receipt) : receipt;
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);
        return new Outcome(receipt.Status, receipt.GasSpent, receipt.Error, Convert.ToHexString(receipt.Output ?? []));
    }

    /// <summary>Records the receipt only, so the virtual machine picks an untraced table.</summary>
    private class ReceiptTracer : TxTracer
    {
        public override bool IsTracingReceipt => true;
        public byte Status { get; private set; } = byte.MaxValue;
        public ulong GasSpent { get; private set; }
        public string? Error { get; private set; }
        public byte[]? Output { get; private set; }

        public override void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
        {
            Status = StatusCode.Success;
            GasSpent = gasSpent.SpentGas;
            Output = output;
        }

        public override void MarkAsFailed(Address recipient, in GasConsumed gasSpent, byte[] output, string? error, Hash256? stateRoot = null)
        {
            Status = StatusCode.Failure;
            GasSpent = gasSpent.SpentGas;
            Error = error;
            Output = output;
        }
    }

    /// <summary>Also traces instructions, so the virtual machine picks the traced table.</summary>
    private sealed class InstructionTracer : ReceiptTracer
    {
        public override bool IsTracingInstructions => true;
    }
}
