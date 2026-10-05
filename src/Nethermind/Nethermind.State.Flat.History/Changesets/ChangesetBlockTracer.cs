// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.State.Flat.History.Changesets;

/// <summary>Writes one changeset row per transaction of the block it traces. Rows land in the batch the caller
/// commits, so a block is indexed whole or not at all.</summary>
internal sealed class ChangesetBlockTracer(TransactionChangesetStore store, IWriteBatch batch) : IBlockTracer, IDisposable
{
    private readonly ChangesetCollector _collector = new();
    private ChangesetTxTracer? _tracer;
    private ulong _block;
    private int _transactionIndex = -1;
    private int _expectedTransactions;
    private bool _everyTransactionObserved;

    public bool IsTracingRewards => false;

    /// <summary>Every transaction of the block was seen and reported something. Every transaction changes at least
    /// its sender's nonce, so an empty changeset means the execution was not observed, as under parallel execution
    /// where the per-worker world states report nothing, and the rows must not be claimed.</summary>
    public bool Complete => _transactionIndex + 1 == _expectedTransactions && _everyTransactionObserved;

    public void ReportReward(Address author, string rewardType, UInt256 rewardValue) { }

    public void StartNewBlockTrace(Block block)
    {
        _block = (ulong)block.Number;
        _transactionIndex = -1;
        _expectedTransactions = block.Transactions.Length;
        _everyTransactionObserved = true;
        store.WriteBlockHash(_block, block.Hash ?? ThrowUnsealed(block), batch);
    }

    /// <summary>One tracer over the one collector, reset per transaction: nothing holds a tracer past EndTxTrace.</summary>
    public ITxTracer StartNewTxTrace(Transaction? tx)
    {
        _collector.Reset();
        _transactionIndex++;
        return _tracer ??= new ChangesetTxTracer(_collector);
    }

    public void EndTxTrace()
    {
        if (_transactionIndex > ChangesetKeyLayout.MaxTransactionIndex) ThrowTooManyTransactions();

        if (_collector.IsEmpty) _everyTransactionObserved = false;
        store.Write(_block, (ushort)_transactionIndex, _collector.IsEmpty ? [] : _collector.Pack(), batch);
    }

    public void EndBlockTrace() { }

    public void Dispose() => _collector.Release();

    private static Hash256 ThrowUnsealed(Block block) =>
        throw new InvalidOperationException($"Block {block.Number} has no hash; the changeset index records which block its rows describe.");

    private void ThrowTooManyTransactions() =>
        throw new InvalidOperationException(
            $"Block {_block} carries more than {ChangesetKeyLayout.MaxTransactionIndex + 1} transactions, which the changeset key cannot address.");
}
