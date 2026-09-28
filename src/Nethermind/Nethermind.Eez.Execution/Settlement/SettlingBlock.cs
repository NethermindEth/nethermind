// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Evm;

namespace Nethermind.Eez.Execution.Settlement;

/// <summary>What the transactions of a window's blocks show about the effects the batch may claim.</summary>
public sealed class SettlingBlock
{
    private SettlingBlock(bool[] systemTransactions, InboundCandidate[] inboundCandidates, OutboundEvent[] outboundEvents)
    {
        SystemTransactions = systemTransactions;
        InboundCandidates = inboundCandidates;
        OutboundEvents = outboundEvents;
        EffectTransactions = EffectTransactionsOf(systemTransactions);
    }

    /// <summary>Whether each transaction is sent by the system address.</summary>
    public bool[] SystemTransactions { get; }

    public InboundCandidate[] InboundCandidates { get; }

    public OutboundEvent[] OutboundEvents { get; }

    /// <summary>The transactions that end an effect, one per effect entry the batch may claim.</summary>
    public int[] EffectTransactions { get; }

    /// <param name="receipts">The block's receipts, one per transaction.</param>
    /// <exception cref="EezSettlementException">A system transaction is malformed or reverted.</exception>
    public static SettlingBlock Inspect(Block block, IReadOnlyList<TxReceipt> receipts, ulong rollupId)
    {
        Transaction[] transactions = block.Transactions;
        if (receipts.Count != transactions.Length)
        {
            throw new EezSettlementException(EezSettlementFailure.InternalInvariant,
                $"The settling block has {transactions.Length} transactions but {receipts.Count} receipts.");
        }

        bool[] system = SystemTransactionsOf(block);
        List<InboundCandidate> candidates = [];
        for (int i = 0; i < transactions.Length; i++)
        {
            if (!system[i])
            {
                continue;
            }

            Transaction transaction = transactions[i];
            if (!transaction.IsEezSystemTransaction() || transaction.To != EezConstants.Eezl2Address)
            {
                throw new EezSettlementException($"Settling transaction {i} uses the system sender without being a system transaction to EEZL2.");
            }

            bool succeeded = receipts[i].StatusCode == StatusCode.Success;
            if (IsDelivery(transaction))
            {
                candidates.Add(Observe(i, transaction, succeeded, rollupId));
            }
            else if (!succeeded)
            {
                throw new EezSettlementException($"Settling system transaction {i} reverted.") { PoisonedTransactionIndex = PairedUser(transactions, system, i) };
            }
        }

        return new SettlingBlock(system, candidates.ToArray(), OutboundEvent.Observe(receipts));
    }

    /// <summary>Blocks before the settling block carry no effects: no system transactions and no outbound calls.</summary>
    /// <exception cref="EezSettlementException">The block carries an effect.</exception>
    public static void EnsureNoEffects(Block block, IReadOnlyList<TxReceipt> receipts)
    {
        Transaction[] transactions = block.Transactions;
        for (int i = 0; i < transactions.Length; i++)
        {
            if (transactions[i].IsEezSystemTransaction() || transactions[i].SenderAddress == EezConstants.SystemAddress)
            {
                throw new EezSettlementException($"Block {block.Number} transaction {i} is a system transaction, but only the settling block may carry one.");
            }
        }

        if (OutboundEvent.Observe(receipts) is [{ } outbound, ..])
        {
            throw new EezSettlementException(
                $"Block {block.Number} transaction {outbound.TransactionIndex} calls out of L2, but only the settling block may carry effects.");
        }
    }

    /// <summary>
    /// The transactions to checkpoint when re-executing <paramref name="block"/> as a settling block: every user
    /// transaction, and every system transaction that no user transaction follows.
    /// </summary>
    public static int[] EffectTransactionsOf(Block block) => EffectTransactionsOf(SystemTransactionsOf(block));

    private static int[] EffectTransactionsOf(bool[] system)
    {
        List<int> positions = new(system.Length);
        for (int i = 0; i < system.Length; i++)
        {
            if (!system[i] || i + 1 == system.Length || system[i + 1])
            {
                positions.Add(i);
            }
        }

        return positions.ToArray();
    }

    private static bool[] SystemTransactionsOf(Block block)
    {
        Transaction[] transactions = block.Transactions;
        bool[] system = new bool[transactions.Length];
        for (int i = 0; i < system.Length; i++)
        {
            system[i] = transactions[i].SenderAddress == EezConstants.SystemAddress;
        }

        return system;
    }

    /// <summary>The user transaction a reverted outbound load stages, if the load is one.</summary>
    private static int? PairedUser(Transaction[] transactions, bool[] system, int load)
    {
        int user = load + 1;
        if (user == transactions.Length || system[user] || !transactions[load].Value.IsZero)
        {
            return null;
        }

        try
        {
            return EezCalldata.DecodeLoadExecutionTable(transactions[load].Data.Span) is { Entries.Length: 1, StaticEntries.Length: 0 } ? user : null;
        }
        catch (EezAbiException)
        {
            return null;
        }
    }

    private static bool IsDelivery(Transaction transaction) =>
        transaction.Data.Length >= sizeof(uint)
        && BinaryPrimitives.ReadUInt32BigEndian(transaction.Data.Span) == EezCalldata.ExecuteIncomingCrossChainCallSelector;

    private static InboundCandidate Observe(int transactionIndex, Transaction transaction, bool succeeded, ulong rollupId)
    {
        try
        {
            return new InboundCandidate(transactionIndex, InboundDelivery.Inspect(transaction.Value, transaction.Data.Span, succeeded, rollupId), null, false);
        }
        catch (EezSettlementException e)
        {
            return new InboundCandidate(transactionIndex, null, e.Message, !succeeded);
        }
    }
}
