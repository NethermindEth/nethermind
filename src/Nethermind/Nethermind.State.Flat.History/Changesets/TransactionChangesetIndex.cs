// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
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
    private readonly Lock[] _blockLocks = [.. Enumerable.Range(0, 64).Select(static _ => new Lock())];

    private Lock BlockLock(ulong block) => _blockLocks[block % (ulong)_blockLocks.Length];

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

    /// <summary>Writes a whole block's changesets, already collected, and claims it when it touches coverage.</summary>
    internal bool WriteBlock(Block block, ReadOnlySpan<ChangesetCollector?> collectors)
    {
        ulong number = (ulong)block.Number;
        if (collectors.Length > ChangesetKeyLayout.MaxTransactionIndex + 1) return false;

        IColumnsWriteBatch<FlatHistoryColumns> batch = _columns.StartWriteBatch();
        try
        {
            IWriteBatch rows = batch.GetColumnBatch(FlatHistoryColumns.TransactionChangesets);
            _store.WriteBlockHash(number, block.Hash!, rows);
            for (int i = 0; i < collectors.Length; i++)
            {
                _store.Write(number, (ushort)i, collectors[i]!.Pack(), rows);
            }
        }
        finally
        {
            lock (BlockLock(number)) batch.Dispose();
        }

        return _store.TryExtendCoverage(number, number);
    }

    /// <summary>Claims a range of durable, complete blocks as covered; only a range touching the existing coverage
    /// is accepted, so coverage stays one contiguous range.</summary>
    public bool TryClaim(ulong fromBlock, ulong toBlock) => _store.TryExtendCoverage(fromBlock, toBlock);

    /// <summary>Follows the history floor: a block whose history is gone cannot be traced, so its changesets have
    /// nothing left to serve.</summary>
    public void PruneBelow(ulong floor)
    {
        if (Enabled) _store.PruneBelow(floor);
    }

    /// <summary>Only for the very block the rows were built from, and only when the rows hold every transaction
    /// before the one asked for; anything else is answered by the replay the node did before the index existed.</summary>
    internal bool TryRentOverlay(ulong block, Hash256 blockHash, ushort beforeTransaction, out MidBlockOverlayCache.Lease lease)
    {
        // Keep the identity check and fold on the same committed version of this height.
        lock (BlockLock(block))
        {
            lease = default;
            return Covers(block)
                && _store.TryGetBlockHash(block, out ValueHash256 indexed)
                && indexed == blockHash
                && _overlays.TryRent(block, in indexed, beforeTransaction, out lease);
        }
    }

    /// <summary>One block's rows, written into a batch of their own. The caller claims coverage only once
    /// <see cref="Commit"/> reports the batch durable and whole, so a builder killed mid-block leaves rows nothing
    /// claims rather than a claim nothing backs.</summary>
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

        /// <summary>False when the tracer did not see the whole block: the rows are written but must not be claimed,
        /// and the next pass builds the block again.</summary>
        public bool Commit()
        {
            if (_written) throw new InvalidOperationException($"The changeset capture of block {_block} was already committed.");

            _written = true;
            lock (_index.BlockLock(_block)) _batch.Dispose();
            return _tracer.Complete;
        }

        public void Dispose()
        {
            _tracer.Dispose();
            if (!_written)
            {
                lock (_index.BlockLock(_block)) _batch.Dispose();
            }
        }
    }
}
