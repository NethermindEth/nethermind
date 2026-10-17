// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Eez.Execution.Settlement;

namespace Nethermind.Eez.Execution.Stateless;

internal sealed class RollupXCheckpointPlan
{
    private readonly int[] _positions;

    private RollupXCheckpointPlan(int ordinaryTransactions, int[] positions)
    {
        OrdinaryTransactions = ordinaryTransactions;
        _positions = positions;
    }

    public int OrdinaryTransactions { get; }
    public ReadOnlySpan<int> Positions => _positions;

    public static RollupXCheckpointPlan Create(Block terminal)
    {
        Transaction[] transactions = terminal.Transactions;
        int ordinary = 0;
        while (ordinary < transactions.Length && !transactions[ordinary].IsEezSystemTransaction()) ordinary++;
        int count = 1;
        for (int i = ordinary; i < transactions.Length; i++)
            if (EndsEffect(transactions, i)) count++;
        int[] positions = new int[count];
        positions[0] = ordinary - 1;
        int next = 1;
        for (int i = ordinary; i < transactions.Length; i++)
            if (EndsEffect(transactions, i)) positions[next++] = i;
        return new RollupXCheckpointPlan(ordinary, positions);
    }

    public void Validate(ReadOnlySpan<EezTransactionCheckpoint> checkpoints)
    {
        if (checkpoints.Length != _positions.Length) throw InvalidCheckpoints();
        for (int i = 0; i < checkpoints.Length; i++)
            if (checkpoints[i].TransactionIndex != _positions[i]) throw InvalidCheckpoints();
    }

    public int[] ObservationPositions(ReadOnlySpan<Hash256> pins, ReadOnlySpan<EezTransactionCheckpoint> checkpoints, Hash256 terminalHash,
        int transactionCount)
    {
        Validate(checkpoints);
        if (transactionCount < OrdinaryTransactions || _positions[^1] != transactionCount - 1) throw InvalidCheckpoints();
        int[] positions = new int[pins.Length];
        int previous = 0;
        for (int i = 0; i < pins.Length; i++)
        {
            int position = Position(pins[i], checkpoints, terminalHash, transactionCount);
            if (position < previous) throw new EezSettlementException("Observation roots are outside FIFO transaction order.");
            positions[i] = previous = position;
        }
        return positions;
    }

    private static int Position(Hash256 pin, ReadOnlySpan<EezTransactionCheckpoint> checkpoints, Hash256 terminalHash, int transactionCount)
    {
        foreach (EezTransactionCheckpoint checkpoint in checkpoints)
            if (checkpoint.BlockHash == pin) return checkpoint.TransactionIndex + 1;
        if (pin == terminalHash) return transactionCount;
        throw new EezSettlementException("Observation root is not a validated transaction prefix.");
    }

    private static bool EndsEffect(Transaction[] transactions, int index) => !transactions[index].IsEezSystemTransaction() ||
        index == transactions.Length - 1 || transactions[index + 1].IsEezSystemTransaction();

    private static EezSettlementException InvalidCheckpoints() => new(EezSettlementFailure.InternalInvariant, "Execution checkpoints differ from the selected transaction positions.");
}
