// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Eez.Execution.Stateless;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Eez.Execution.Settlement.RollupX;

internal sealed partial class NativeTransactions
{
    /// <summary>Matches a whole-group suffix against the complete original semantic stream.</summary>
    /// <remarks>Execution, the L1 parent, settlement tables and any consumed prefix still require independent authorization.</remarks>
    internal void VerifyNativeSuffix(EezStatelessBlockResult execution, int firstTransaction, ProducingSlice applied,
        NativeLiveSource source, EezChainRules rules)
    {
        Transaction[] transactions = execution.Block.Transactions;
        int count = checked(_outbound.Count + _inbound.Count);
        if (!rules.UsesNativeContract || source.ChainId != _chainId || firstTransaction < 0 || firstTransaction > transactions.Length ||
            applied.Skip < 0 || applied.Take < 0 || applied.Skip > count || applied.Take > count - applied.Skip ||
            execution.Receipts.Length != transactions.Length)
            throw NativeCallTree.Invalid("invalid native reconstruction profile or suffix bounds");

        int outbound = Math.Min(applied.Take, Math.Max(0, _outbound.Count - applied.Skip));
        if ((long)applied.Take + outbound != transactions.Length - firstTransaction)
            throw NativeCallTree.Invalid("native reconstruction does not cover the exact transaction suffix");

        ulong previous = 0;
        NativeExecutionGroup prefix = default;
        for (int index = 0; index < transactions.Length; index++)
        {
            ulong cumulative = execution.Receipts[index].GasUsedTotal;
            if (cumulative < previous || cumulative > execution.Block.GasLimit)
                throw NativeCallTree.Invalid("native reconstruction has inconsistent cumulative gas");
            previous = cumulative;
            if (index < firstTransaction) prefix = prefix.After(transactions[index], out _);
        }
        if (previous != execution.Block.Header.GasUsed)
            throw NativeCallTree.Invalid("native reconstruction gas does not cover the executed block");
        prefix.EnsureComplete();

        int position = firstTransaction;
        int end = applied.Skip + applied.Take;
        for (int group = applied.Skip; group < end; group++)
        {
            if (group < _outbound.Count)
            {
                (ExecutionTable table, byte[] user) = _outbound[group];
                VerifyNativeTransaction(execution, position++, source, group, NativeValue.Funding(table.Entries),
                    EezCalldata.EncodeLoadExecutionTable(table), rules);
                if (position >= transactions.Length || transactions[position].IsEezSystemTransaction() ||
                    transactions[position].SenderAddress == rules.SystemAddress ||
                    !TxDecoder.Instance.Encode(transactions[position], RlpBehaviors.SkipTypedWrapping).Bytes.AsSpan().SequenceEqual(user))
                    throw NativeCallTree.Invalid("native outbound group differs from its original signed user transaction");
                position++;
            }
            else
            {
                L2ExecutionEntry[] entries = [_inbound[group - _outbound.Count]];
                VerifyNativeTransaction(execution, position++, source, group, NativeValue.Funding(entries),
                    EezCalldata.EncodeExecuteIncomingCrossChainCall(new IncomingCrossChainCall(entries, [])), rules);
            }
        }
        if (position != transactions.Length)
            throw NativeCallTree.Invalid("native reconstruction does not cover the exact transaction suffix");
    }

    private void VerifyNativeTransaction(EezStatelessBlockResult execution, int position, NativeLiveSource source, int group,
        in UInt256 value, ReadOnlySpan<byte> calldata, EezChainRules rules)
    {
        if (position >= execution.Block.Transactions.Length)
            throw NativeCallTree.Invalid("native reconstruction is missing a complete group");
        Transaction transaction = execution.Block.Transactions[position];
        ulong priorGas = position == 0 ? 0 : execution.Receipts[position - 1].GasUsedTotal;
        ulong gasLimit = Math.Min(execution.Block.GasLimit - priorGas, EezChainRules.RollupXSystemTxGasCap);
        if (transaction.Type != EezConstants.NativeSystemTxType || !transaction.HasSystemTransactionFields(rules) ||
            transaction.ChainId != _chainId || transaction.SourceHash?.ValueHash256 != source.Entry((ulong)group) ||
            transaction.GasLimit != gasLimit || transaction.To != rules.Eezl2Address || transaction.Value != value ||
            !transaction.Data.Span.SequenceEqual(calldata))
            throw NativeCallTree.Invalid("native transaction differs from its original group and executed position");
    }
}
