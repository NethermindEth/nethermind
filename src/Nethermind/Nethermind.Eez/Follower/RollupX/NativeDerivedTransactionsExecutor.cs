// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Proofs;

namespace Nethermind.Eez.Follower.RollupX;

internal sealed class NativeDerivedSuffix
{
    internal int FirstTransaction { get; set; } = -1;
    internal bool DeriveOrigin { get; set; }
}

internal sealed class NativeDerivedTransactionsExecutor(
    ITransactionProcessorAdapter processor,
    IWorldState state,
    NativeDerivedSuffix suffix) : BlockProcessor.BlockValidationTransactionsExecutor(processor, state), IBlockProcessor.IBlockTransactionsExecutor
{
    private bool _changed;

    public new TxReceipt[] ProcessTransactions(Block block, ProcessingOptions options, BlockReceiptsTracer tracer, CancellationToken token)
    {
        _changed = false;
        TxReceipt[] receipts = base.ProcessTransactions(block, options, tracer, token);
        if (_changed) block.Header.TxRoot = TxTrie.CalculateRoot(block.Transactions);
        return receipts;
    }

    protected override void ProcessTransaction(Block block, Transaction transaction, int index, BlockReceiptsTracer tracer, ProcessingOptions options)
    {
        if (suffix.FirstTransaction >= 0 && index >= suffix.FirstTransaction && transaction.Type == EezConstants.NativeSystemTxType)
        {
            if (block.Header.GasUsed > block.GasLimit) throw NativeInvalid(block);
            ulong gas = Math.Min(block.GasLimit - block.Header.GasUsed, EezChainRules.RollupXSystemTxGasCap);
            if (transaction.GasLimit != gas)
            {
                transaction.Hash = null;
                transaction.GasLimit = gas;
                transaction.Hash = Keccak.Compute(TxDecoder.Instance.Encode(transaction, RlpBehaviors.SkipTypedWrapping).Bytes);
                _changed = true;
            }
        }
        base.ProcessTransaction(block, transaction, index, tracer, options);
    }

    private static EezSettlementException NativeInvalid(Block block) => new($"Native derived block {block.Number} exceeds its gas limit.");
}
