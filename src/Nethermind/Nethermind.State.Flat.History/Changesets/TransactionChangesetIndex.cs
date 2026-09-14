// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Db;
using Nethermind.Evm.Tracing;

namespace Nethermind.State.Flat.History.Changesets;

/// <summary>The per-transaction changeset column: what each transaction of a covered block wrote, and the overlay
/// that answers a read at a transaction boundary from it. A block outside the coverage is served the way it was
/// before the column existed, so a half-built index changes latency and never an answer.</summary>
public sealed class TransactionChangesetIndex
{
    private readonly IColumnsDb<FlatHistoryColumns> _columns;
    private readonly TransactionChangesetStore _store;
    private readonly MidBlockOverlayCache _overlays;

    public TransactionChangesetIndex(IColumnsDb<FlatHistoryColumns> columns, IFlatDbConfig config)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(config);

        _columns = columns;
        _store = new TransactionChangesetStore(columns.GetColumnDb(FlatHistoryColumns.TransactionChangesets));
        _overlays = new MidBlockOverlayCache(_store);
        Enabled = config.HistoryTransactionIndexEnabled;
    }

    public bool Enabled { get; }

    public bool Covers(ulong block) => Enabled && _store.Covers(block);

    public bool TryGetCoverage(out ulong fromBlock, out ulong toBlock) => _store.TryGetCoverage(out fromBlock, out toBlock);

    public BlockCapture StartBlock(ulong block) => new(this, block);

    internal bool TryRentOverlay(ulong block, ushort beforeTransaction, out MidBlockOverlayCache.Lease lease)
    {
        if (!Covers(block))
        {
            lease = default;
            return false;
        }

        lease = _overlays.Rent(block, beforeTransaction);
        return true;
    }

    /// <summary>One block's rows, written into a batch of their own. Coverage moves only once that batch is
    /// durable, so a builder killed mid-block leaves rows nothing claims rather than a claim nothing backs.</summary>
    public sealed class BlockCapture : IDisposable
    {
        private readonly TransactionChangesetIndex _index;
        private readonly ulong _block;
        private readonly IColumnsWriteBatch<FlatHistoryColumns> _batch;
        private readonly ChangesetBlockTracer _tracer;
        private bool _written;

        internal BlockCapture(TransactionChangesetIndex index, ulong block)
        {
            _index = index;
            _block = block;
            _batch = index._columns.StartWriteBatch();
            _tracer = new ChangesetBlockTracer(index._store, _batch.GetColumnBatch(FlatHistoryColumns.TransactionChangesets));
        }

        public IBlockTracer Tracer => _tracer;

        public ushort TransactionsWritten => _tracer.TransactionsWritten;

        public bool Commit()
        {
            if (_written) throw new InvalidOperationException($"The changeset capture of block {_block} was already committed.");

            _written = true;
            _batch.Dispose();
            return _index._store.TryExtendCoverage(_block, _block);
        }

        public void Dispose()
        {
            _tracer.Dispose();
            if (!_written) _batch.Dispose();
        }
    }
}
