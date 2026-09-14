// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.State.Flat.History.Changesets;

/// <summary>Writes one changeset row per transaction of the block it traces. Rows land in the batch the caller
/// commits, so a block is indexed whole or not at all.</summary>
internal sealed class ChangesetBlockTracer(TransactionChangesetStore store, IWriteBatch batch) : IBlockTracer, IDisposable
{
    private readonly ChangesetCollector _collector = new();
    private ulong _block;
    private int _transactionIndex = -1;

    public bool IsTracingRewards => false;

    public ushort TransactionsWritten { get; private set; }

    public void ReportReward(Address author, string rewardType, UInt256 rewardValue) { }

    public void StartNewBlockTrace(Block block)
    {
        _block = (ulong)block.Number;
        _transactionIndex = -1;
        TransactionsWritten = 0;
    }

    public ITxTracer StartNewTxTrace(Transaction? tx)
    {
        _collector.Reset();
        _transactionIndex++;
        return new ChangesetTxTracer(_collector);
    }

    public void EndTxTrace()
    {
        if (_transactionIndex > ChangesetKeyLayout.MaxTransactionIndex) ThrowTooManyTransactions();

        store.Write(_block, (ushort)_transactionIndex, _collector.IsEmpty ? [] : _collector.Pack(), batch);
        TransactionsWritten++;
    }

    public void EndBlockTrace() { }

    public void Dispose() => _collector.Release();

    private void ThrowTooManyTransactions() =>
        throw new InvalidOperationException(
            $"Block {_block} carries more than {ChangesetKeyLayout.MaxTransactionIndex + 1} transactions, which the changeset key cannot address.");
}
