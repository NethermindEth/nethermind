// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.OpcodeTracing.Plugin.Tracing;
using NUnit.Framework;

namespace Nethermind.OpcodeTracing.Plugin.Test;

public class OpcodeBlockTracerTests
{
    [Test]
    public void EndTxTrace_accumulates_transaction_trace()
    {
        OpcodeBlockTrace? completedTrace = null;
        OpcodeBlockTracer blockTracer = new(trace => completedTrace = trace);
        Transaction transaction = new() { GasLimit = 1 };

        blockTracer.StartNewBlockTrace(new Block(CreateHeader(), [transaction], []));
        ITxTracer txTracer = blockTracer.StartNewTxTrace(transaction);
        txTracer.StartOperation(0, Instruction.ADD, 0, null!);
        blockTracer.EndTxTrace();
        blockTracer.EndBlockTrace();

        Assert.That(completedTrace, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(completedTrace!.TransactionCount, Is.EqualTo(1));
            Assert.That(completedTrace.Opcodes[(byte)Instruction.ADD], Is.EqualTo(1));
        });
    }

    [Test]
    public void Counts_all_transaction_traces_when_completed_in_parallel()
    {
        const int txCount = 128;
        OpcodeBlockTrace? completedTrace = null;
        OpcodeBlockTracer blockTracer = new(trace => completedTrace = trace);
        Transaction[] transactions = new Transaction[txCount];
        for (int i = 0; i < transactions.Length; i++)
        {
            transactions[i] = new Transaction { GasLimit = 1 };
        }

        Block block = new(CreateHeader(), transactions, []);
        blockTracer.StartNewBlockTrace(block);

        Parallel.For(0, txCount, i =>
        {
            using ITxTracer txTracer = blockTracer.StartNewTxTrace(transactions[i]);
            txTracer.StartOperation(0, Instruction.ADD, 0, null!);
            txTracer.StartOperation(1, Instruction.STOP, 0, null!);
            blockTracer.EndTxTrace();
        });
        blockTracer.EndBlockTrace();

        Assert.That(completedTrace, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(completedTrace!.TransactionCount, Is.EqualTo(txCount));
            Assert.That(completedTrace.Opcodes[(byte)Instruction.ADD], Is.EqualTo(txCount));
            Assert.That(completedTrace.Opcodes[(byte)Instruction.STOP], Is.EqualTo(txCount));
        });
    }

    private static BlockHeader CreateHeader() =>
        new(
            Keccak.Zero,
            Keccak.OfAnEmptySequenceRlp,
            Address.Zero,
            UInt256.Zero,
            number: 1,
            gasLimit: 1_000_000,
            timestamp: 1,
            extraData: []);
}
