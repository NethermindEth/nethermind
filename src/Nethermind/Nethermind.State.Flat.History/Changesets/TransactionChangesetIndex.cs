// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
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
    private readonly ConsecutiveBlockOverlays _consecutive = new();
    private readonly Lock[] _blockLocks = new Lock[64];
    private readonly long[] _blockVersions = new long[64];

    private Lock BlockLock(ulong block) => _blockLocks[block % (ulong)_blockLocks.Length];

    private readonly ISpecProvider? _specProvider;

    public TransactionChangesetIndex(IColumnsDb<FlatHistoryColumns> columns, IFlatDbConfig config, ISpecProvider? specProvider = null)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(config);

        _specProvider = specProvider;
        for (int i = 0; i < _blockLocks.Length; i++) _blockLocks[i] = new Lock();

        _columns = columns;
        _store = new TransactionChangesetStore(columns.GetColumnDb(FlatHistoryColumns.TransactionChangesets));
        _overlays = new MidBlockOverlayCache(_store);
        Enabled = config.HistoryTransactionIndexEnabled;
    }

    /// <summary>Whether capture and indexed reads are enabled by configuration.</summary>
    public bool Enabled { get; }

    /// <summary>Whether the enabled index claims complete rows at this height; readers also validate block identity.</summary>
    public bool Covers(ulong block) => Enabled && _store.Covers(block);

    /// <summary>Returns the inclusive contiguous stored range. Outputs are ignored when false.</summary>
    public bool TryGetCoverage(out ulong fromBlock, out ulong toBlock) => _store.TryGetCoverage(out fromBlock, out toBlock);

    /// <summary>Publishes the covered range, so that what the index can answer is visible from outside the process:
    /// a retrofit's progress, and the range a trace benchmark has to stay inside to be measuring the index at all.</summary>
    public void ReportCoverage()
    {
        if (!Enabled || !TryGetCoverage(out ulong from, out ulong to))
        {
            Flat.Metrics.TransactionChangesetIndexFrom = 0;
            Flat.Metrics.TransactionChangesetIndexTo = 0;
            return;
        }

        Flat.Metrics.TransactionChangesetIndexFrom = (long)from;
        Flat.Metrics.TransactionChangesetIndexTo = (long)to;
    }

    /// <summary>Creates a caller-owned capture. Dispose after committing, or to discard an incomplete capture.</summary>
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
        catch
        {
            batch.Clear();
            throw;
        }
        finally
        {
            CommitBatch(number, batch);
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
        long version;
        ValueHash256 indexed;
        lease = default;
        lock (BlockLock(block))
        {
            if (!Covers(block) || !_store.TryGetBlockHash(block, out indexed) || indexed != blockHash) return false;
            version = _blockVersions[block % (ulong)_blockVersions.Length];
        }

        if (!_overlays.TryRent(block, in indexed, beforeTransaction, out lease, version)) return false;
        lock (BlockLock(block))
        {
            if (version == _blockVersions[block % (ulong)_blockVersions.Length] && Covers(block)) return true;
        }
        lease.Dispose();
        lease = default;
        return false;
    }

    private void CommitBatch(ulong block, IColumnsWriteBatch<FlatHistoryColumns> batch)
    {
        lock (BlockLock(block))
        {
            try
            {
                batch.Dispose();
            }
            finally
            {
                _blockVersions[block % (ulong)_blockVersions.Length]++;
            }
        }
    }

    /// <summary>The whole block for a trace of every transaction: rows in memory, and the chain of the consecutive
    /// blocks traced before it when the block continues one. A block joins the chain only when the chain can refuse
    /// every address the block wrote after its transactions, which needs the block's fork and proof of stake.</summary>
    internal bool TryOpenBlock(Block block, [NotNullWhen(true)] out ICoveredBlock? covered)
    {
        covered = null;
        ulong number = (ulong)block.Number;
        if (!Covers(number) || block.Hash is null || block.ParentHash is null) return false;
        long version;
        lock (BlockLock(number))
        {
            version = _blockVersions[number % (ulong)_blockVersions.Length];
        }
        if (!BlockChangesets.TryRead(_store, number, block.Hash, block.Transactions.Length, out BlockChangesets? rows)) return false;
        lock (BlockLock(number))
        {
            if (version != _blockVersions[number % (ulong)_blockVersions.Length] || !Covers(number)) return false;
        }

        HashSet<AddressAsKey> excluded = [];
        // IsPostMerge is set while a block is processed and is not decoded from a stored header, so a block read back
        // for a trace would never chain; the difficulty the header does carry is what says proof of stake.
        bool chainable = block.Header.IsPoS()
            && _specProvider is not null
            && PostTransactionWriters.Describes(_specProvider.SealEngine)
            && PostTransactionWriters.TryCollect(block, _specProvider.GetSpec(block.Header), excluded);
        covered = new CoveredBlock(rows, number == 0 ? null : _consecutive.EndingAt(number - 1, block.ParentHash), chainable ? _consecutive : null, excluded);
        return true;
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

        /// <summary>Records one block into this capture; valid until disposal.</summary>
        public IBlockTracer Tracer => _tracer;

        /// <summary>Publishes a complete capture; otherwise discards the batch so an existing block stays intact.</summary>
        public bool Commit()
        {
            if (_written) throw new InvalidOperationException($"The changeset capture of block {_block} was already committed.");

            _written = true;
            if (!_tracer.Complete) _batch.Clear();
            _index.CommitBatch(_block, _batch);
            return _tracer.Complete;
        }

        /// <summary>Discards uncommitted rows and releases the tracer. Committed rows remain durable.</summary>
        public void Dispose()
        {
            _tracer.Dispose();
            if (!_written)
            {
                _batch.Clear();
                _index.CommitBatch(_block, _batch);
            }
        }
    }
}
