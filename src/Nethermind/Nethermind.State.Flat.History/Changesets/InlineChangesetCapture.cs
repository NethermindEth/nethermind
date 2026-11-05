// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.State.Flat.History.Changesets;

/// <summary>Rides along on main block processing while the node is syncing, so a node that executes every block
/// once anyway gets its changesets for the cost of a dictionary write per state change and no second execution.
/// Near the tip it steps aside: reorgs are the out-of-band builder's problem, which only ever indexes blocks the
/// history capture has made durable. Under parallel execution the per-worker world states report no changes; a
/// block where any transaction reported nothing is therefore never written, and the builder indexes it later.
/// Rows are written here but coverage is never claimed here: capture runs before the block is validated, so the
/// builder claims the height once it sees these rows carry the hash of the canonical block.</summary>
public sealed class InlineChangesetCapture(TransactionChangesetIndex index, IInlineCapturePolicy policy, ILogManager logManager) : IParallelSafeBlockTracer
{
    private readonly ILogger _logger = logManager.GetClassLogger<InlineChangesetCapture>();
    private readonly Dictionary<Transaction, int> _positions = new(ReferenceEqualityComparer.Instance);
    private ChangesetCollector?[] _collectors = [];
    private ChangesetTxTracer?[] _tracers = [];
    private Block? _block;
    private bool _active;
    private bool _sawUnknownTransaction;

    public bool IsTracingRewards => false;

    public void ReportReward(Address author, string rewardType, UInt256 rewardValue)
    {
    }

    public void StartNewBlockTrace(Block block)
    {
        _block = block;
        _active = index.Enabled && block.Hash is not null && policy.ShouldCapture(block);
        if (!_active) return;

        Transaction[] transactions = block.Transactions;
        if (_collectors.Length < transactions.Length)
        {
            Array.Resize(ref _collectors, transactions.Length);
            Array.Resize(ref _tracers, transactions.Length);
        }

        for (int i = 0; i < transactions.Length; i++) _collectors[i]?.Reset();
        _positions.Clear();
        _sawUnknownTransaction = false;
        for (int i = 0; i < transactions.Length; i++) _positions[transactions[i]] = i;
    }

    public ITxTracer StartNewTxTrace(Transaction? tx)
    {
        if (!_active) return NullTxTracer.Instance;
        if (tx is null || !_positions.TryGetValue(tx, out int position))
        {
            _sawUnknownTransaction = true;
            if (_logger.IsDebug) _logger.Debug($"Inline changeset capture of block {_block!.Number} saw a transaction not in the block; the block will be left to the builder.");
            return NullTxTracer.Instance;
        }

        // Collectors and their tracers are kept per position and reset between blocks, so their dictionaries stay
        // warm instead of being reallocated for every transaction of every captured block.
        ChangesetCollector collector = _collectors[position] ??= new ChangesetCollector();
        return _tracers[position] ??= new ChangesetTxTracer(collector);
    }

    public void EndTxTrace()
    {
    }

    public void EndBlockTrace()
    {
        if (!_active) return;

        Block block = _block!;
        try
        {
            if (EveryTransactionObserved(block)) index.WriteBlock(block, _collectors.AsSpan(0, block.Transactions.Length));
        }
        catch (Exception exception)
        {
            if (_logger.IsWarn) _logger.Warn($"Inline changeset capture of block {block.Number} failed; the builder will index it: {exception.Message}");
        }
        finally
        {
            foreach (ChangesetCollector? collector in _collectors) collector?.Reset();
            _positions.Clear();
            _active = false;
        }
    }

    /// <summary>Every transaction changes at least its sender's nonce, so an empty changeset means the execution
    /// was not observed, not that nothing happened. A transaction that was traced without being one of the block's
    /// own leaves writes nowhere, so the block is left to the builder even when every position is filled.</summary>
    private bool EveryTransactionObserved(Block block)
    {
        if (_sawUnknownTransaction) return false;

        for (int i = 0; i < block.Transactions.Length; i++)
        {
            if (_collectors[i] is not { IsEmpty: false }) return false;
        }

        return true;
    }
}
