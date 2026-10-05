// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Merge.Plugin.Synchronization;

/// <summary>Reconstructs finalized catch-up state, falling back to execution when commitments cannot be reproduced.</summary>
public sealed class FinalizedBlockAccessListProcessor(
    IBlockProcessor inner,
    FinalizedBlockAccessListPolicy policy,
    IWorldState state,
    IReceiptStorage receiptStorage,
    ILogManager logManager) : IBlockProcessor
{
    private readonly ILogger _logger = logManager.GetClassLogger<FinalizedBlockAccessListProcessor>();
    private event Action? _transactionsExecuted;

    /// <summary>The processor that executes blocks this decorator does not reconstruct.</summary>
    public IBlockProcessor Inner => inner;

    /// <inheritdoc/>
    public event Action? TransactionsExecuted
    {
        add { inner.TransactionsExecuted += value; _transactionsExecuted += value; }
        remove { inner.TransactionsExecuted -= value; _transactionsExecuted -= value; }
    }

    /// <inheritdoc/>
    public (Block Block, TxReceipt[] Receipts) ProcessOne(Block block, ProcessingOptions options,
        IBlockTracer blockTracer, IReleaseSpec spec, CancellationToken token = default)
    {
        const ProcessingOptions excluded = ProcessingOptions.ReadOnlyChain | ProcessingOptions.ForceProcessing
            | ProcessingOptions.NoValidation | ProcessingOptions.DoNotUpdateHead | ProcessingOptions.ForceSequentialBlockAccessList;
        if ((options & excluded) != 0 || blockTracer != NullBlockTracer.Instance || !policy.CanReconstruct(block.Header))
            return inner.ProcessOne(block, options, blockTracer, spec, token);

        ReadOnlyBlockAccessList? list = block.BlockAccessList;
        try
        {
            if (list is null && block.EncodedBlockAccessList is { } encoded)
                list = Rlp.Decode<ReadOnlyBlockAccessList>(encoded);
        }
        catch (RlpException ex)
        {
            return Execute($"undecodable block access list: {ex.Message}");
        }

        if (list is null) return Execute("no block access list");
        if ((list.WireHash ?? Keccak.Compute(Rlp.Encode(list).Bytes)) != block.Header.BlockAccessListHash)
            return Execute("block access list hash mismatch");

        TxReceipt[] receipts = [];
        if (block.Transactions.Length > 0)
        {
            if (!receiptStorage.HasBlock(block.Number, block.Hash!)) return Execute("no receipts");
            receipts = receiptStorage.Get(block);
            if (receipts.Length != block.Transactions.Length
                || ReceiptsRootCalculator.Instance.GetReceiptsRoot(receipts, spec, block.ReceiptsRoot) != block.ReceiptsRoot)
                return Execute("receipts root mismatch");
        }

        // No transactions run, so background work such as prewarming can stop before the writes are applied.
        _transactionsExecuted?.Invoke();
        BlockAccessListStateReconstructor.Apply(state, list, spec, token);
        state.Commit(spec);
        state.RecalculateStateRoot();
        if (state.StateRoot != block.StateRoot)
            throw new BlockProcessor.BlockAccessListSequentialRetryException(block.Header,
                $"BAL reconstruction mismatched state root for {block}; retrying execution.");
        block.BlockAccessList = list;
        block.AccountChanges = state.GetAccountChanges();
        Metrics.FinalizedBlockAccessListReconstructions++;
        return (block, receipts);

        (Block, TxReceipt[]) Execute(string reason)
        {
            if (_logger.IsDebug) _logger.Debug($"Executing finalized {block.ToString(Block.Format.Short)}: {reason}");
            return inner.ProcessOne(block, options, blockTracer, spec, token);
        }
    }
}
