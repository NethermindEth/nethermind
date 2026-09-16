// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.Consensus.Tracing;

/// <summary>Marks completion of a requested transaction without changing the block presented to tracers.</summary>
public sealed class TransactionTraceBoundary : IBlockTracer
{
    private readonly IBlockTracer _inner;
    private readonly Hash256? _transactionHash;
    private readonly IPrefixStateSeedSource? _seeds;

    private TransactionTraceBoundary(IBlockTracer inner, Hash256? transactionHash, IPrefixStateSeedSource? seeds)
    {
        _inner = inner;
        _transactionHash = transactionHash;
        _seeds = seeds;
    }

    private bool _isTarget;
    internal bool IsComplete { get; private set; }
    internal IBlockTracer Inner => _inner;

    internal IPrefixStateSeedSource? Seeds => _seeds;

    /// <summary>No transaction is the target: the seed stands for the whole block and only what follows the
    /// transactions, the rewards, is executed and traced.</summary>
    internal bool SkipsTransactions => _transactionHash is null;

    /// <summary>Wraps a transaction tracer for early completion in a supported read-only replay environment.</summary>
    /// <param name="tracer">The tracer to forward callbacks to; reward tracing retains full replay.</param>
    /// <param name="transactionHash">The transaction to stop after, or null for unrestricted replay.</param>
    /// <param name="seeds">Where the state before the target may come from instead of replaying the prefix.</param>
    /// <returns>The original tracer for a null hash or reward tracing; otherwise a completion boundary.</returns>
    public static IBlockTracer Wrap(IBlockTracer tracer, Hash256? transactionHash, IPrefixStateSeedSource? seeds = null) =>
        transactionHash is null || tracer.IsTracingRewards ? tracer : new TransactionTraceBoundary(tracer, transactionHash, seeds);

    /// <summary>Wraps a tracer that wants only what comes after the transactions: the seed for the end of the block
    /// stays armed through the rewards and withdrawals, so they are applied and traced on the state the last
    /// transaction left, as in the replay. A refused seed replays the block, so the answer is always right.</summary>
    public static IBlockTracer AfterTransactions(IBlockTracer tracer, IPrefixStateSeedSource seeds) => new TransactionTraceBoundary(tracer, null, seeds);

    internal int IndexOf(Block block)
    {
        Transaction[] transactions = block.Transactions;
        if (_transactionHash is null) return transactions.Length;
        for (int i = 0; i < transactions.Length; i++)
        {
            if (transactions[i].Hash == _transactionHash) return i;
        }

        return -1;
    }

    internal static TransactionTraceBoundary? Get(IBlockTracer tracer, ProcessingOptions options) =>
        options.ContainsFlag(ProcessingOptions.ReadOnlyChain | ProcessingOptions.NoValidation)
        && !options.ContainsFlag(ProcessingOptions.StoreReceipts)
            ? tracer as TransactionTraceBoundary : null;

    public bool IsTracingRewards => _inner.IsTracingRewards;

    public void ReportReward(Address author, string rewardType, UInt256 rewardValue) =>
        _inner.ReportReward(author, rewardType, rewardValue);

    public void StartNewBlockTrace(Block block)
    {
        IsComplete = false;
        _isTarget = false;
        _inner.StartNewBlockTrace(block);
    }

    public ITxTracer StartNewTxTrace(Transaction? tx)
    {
        _isTarget = _transactionHash is not null && tx?.Hash == _transactionHash;
        return _inner.StartNewTxTrace(tx);
    }

    public void EndTxTrace()
    {
        _inner.EndTxTrace();
        IsComplete |= _isTarget && !_inner.IsTracingRewards;
        _isTarget = false;
    }

    public void EndBlockTrace() => _inner.EndBlockTrace();
}
