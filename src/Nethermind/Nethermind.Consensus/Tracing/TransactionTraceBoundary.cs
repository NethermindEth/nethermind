// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
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
    private bool _suppressed;
    internal bool IsComplete { get; private set; }
    internal IBlockTracer Inner => _inner;

    internal IPrefixStateSeedSource? Seeds => _seeds;
    internal bool IsExecutionRequired { get; set; }
    internal bool HasExecuted { get; set; }
    private bool CompletesBeforeTransactions { get; init; }

    /// <summary>No transaction is traced: a seed can replace the whole block, or a refused seed can replay it
    /// silently before tracing rewards.</summary>
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
    /// transaction left, as in the replay. A refused seed replays transactions without forwarding their traces.</summary>
    public static TransactionTraceBoundary AfterTransactions(IBlockTracer tracer, IPrefixStateSeedSource seeds) => new(tracer, null, seeds) { IsExecutionRequired = true };

    internal static IBlockTracer BetweenTransactions(IBlockTracer tracer, Transaction[] transactions, int first, int last, IPrefixStateSeedSource seeds, CancellationToken token) =>
        tracer.IsTracingRewards
            ? tracer
            : new TransactionTraceBoundary(tracer, transactions[last].Hash,
                first == 0 && last == 0 ? seeds : new RangeSeeds(seeds, first, transactions[first].Hash!, token));

    /// <summary>Wraps a tracer that does not trace rewards so that the replay completes just before transaction
    /// <paramref name="index"/>; for index 0 that is after the pre-execution system calls.</summary>
    internal static TransactionTraceBoundary BeforeTransaction(IBlockTracer tracer, Block block, int index) =>
        index == 0
            ? new(tracer, null, null) { CompletesBeforeTransactions = true }
            : new(tracer, block.Transactions[index - 1].Hash, null);

    internal int IndexOf(Block block)
    {
        Transaction[] transactions = block.Transactions;
        if (_seeds is RangeSeeds range)
            return (uint)range.First < (uint)transactions.Length && transactions[range.First].Hash == range.FirstHash ? range.First : -1;
        if (_transactionHash is null) return transactions.Length;
        for (int i = 0; i < transactions.Length; i++)
        {
            if (transactions[i].Hash == _transactionHash) return i;
        }

        return -1;
    }

    internal static TransactionTraceBoundary? Get(IBlockTracer tracer, ProcessingOptions options) =>
        IsUnpersistedReplay(options) ? tracer as TransactionTraceBoundary : null;

    /// <summary>A read-only, unvalidated replay whose receipts are not stored: nothing reads what it finalizes.</summary>
    internal static bool IsUnpersistedReplay(ProcessingOptions options) =>
        options.ContainsFlag(ProcessingOptions.ReadOnlyChain | ProcessingOptions.NoValidation)
        && !options.ContainsFlag(ProcessingOptions.StoreReceipts);

    public bool IsTracingRewards => _inner.IsTracingRewards;

    public void ReportReward(Address author, string rewardType, UInt256 rewardValue) =>
        _inner.ReportReward(author, rewardType, rewardValue);

    public void StartNewBlockTrace(Block block)
    {
        IsComplete = CompletesBeforeTransactions;
        HasExecuted = false;
        _isTarget = false;
        _suppressed = false;
        if (_seeds is RangeSeeds range) range.Reset();
        _inner.StartNewBlockTrace(block);
    }

    public ITxTracer StartNewTxTrace(Transaction? tx)
    {
        // Reward placeholders have no transaction and must reach the inner tracer even during a reward-only pass.
        if (_seeds is RangeSeeds range && !range.Reached(tx))
        {
            _suppressed = true;
            return range.SuppressedTracer;
        }
        _suppressed = SkipsTransactions && tx is not null;
        if (_suppressed) return NullTxTracer.Instance;
        _isTarget = _transactionHash is not null && tx?.Hash == _transactionHash;
        return _inner.StartNewTxTrace(tx);
    }

    public void EndTxTrace()
    {
        if (_suppressed)
        {
            _suppressed = false;
            return;
        }
        _inner.EndTxTrace();
        IsComplete |= _isTarget && !_inner.IsTracingRewards;
        _isTarget = false;
    }

    public void EndBlockTrace() => _inner.EndBlockTrace();

    private sealed class RangeSeeds(IPrefixStateSeedSource inner, int first, Hash256 firstHash, CancellationToken token) : IPrefixStateSeedSource
    {
        private bool _reached;
        private CancellationTxTracer? _suppressedTracer;

        public int First => first;
        public Hash256 FirstHash => firstHash;
        public bool Enabled => inner.Enabled;
        public ITxTracer SuppressedTracer => _suppressedTracer ??= NullTxTracer.Instance.WithCancellation(token);

        public void Reset() => _reached = false;

        public bool Reached(Transaction? transaction) => _reached |= transaction?.Hash == firstHash;

        public bool TrySeed(Block block, int transactionIndex, StateReadOverlaySlot slot) => inner.TrySeed(block, transactionIndex, slot);

        public bool TryOpenBlock(Block block, [NotNullWhen(true)] out ICoveredBlock? covered) => inner.TryOpenBlock(block, out covered);
    }
}
