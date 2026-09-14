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
/// block where any transaction reported nothing is therefore never claimed, and the builder indexes it later.</summary>
public sealed class InlineChangesetCapture(TransactionChangesetIndex index, Func<Block, bool> shouldCapture, ILogManager logManager) : IParallelSafeBlockTracer
{
    private readonly ILogger _logger = logManager.GetClassLogger<InlineChangesetCapture>();
    private ChangesetCollector?[] _collectors = [];
    private Block? _block;
    private bool _active;

    public bool IsTracingRewards => false;

    public void ReportReward(Address author, string rewardType, UInt256 rewardValue)
    {
    }

    public void StartNewBlockTrace(Block block)
    {
        _block = block;
        _active = index.Enabled && block.Hash is not null && block.Transactions.Length > 0 && shouldCapture(block);
        if (!_active) return;

        if (_collectors.Length < block.Transactions.Length) _collectors = new ChangesetCollector?[block.Transactions.Length];
        Array.Clear(_collectors);
    }

    public ITxTracer StartNewTxTrace(Transaction? tx)
    {
        if (!_active || tx is null) return NullTxTracer.Instance;

        int position = Array.IndexOf(_block!.Transactions, tx);
        if (position < 0) return NullTxTracer.Instance;

        ChangesetCollector collector = new();
        _collectors[position] = collector;
        return new ChangesetTxTracer(collector);
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
            foreach (ChangesetCollector? collector in _collectors) collector?.Release();
            Array.Clear(_collectors);
            _active = false;
        }
    }

    /// <summary>Every transaction changes at least its sender's nonce, so an empty changeset means the execution
    /// was not observed, not that nothing happened.</summary>
    private bool EveryTransactionObserved(Block block)
    {
        for (int i = 0; i < block.Transactions.Length; i++)
        {
            if (_collectors[i] is not { IsEmpty: false }) return false;
        }

        return true;
    }
}
