// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Serialization.Rlp;
using Columns = Nethermind.State.Flat.History.Changesets.BulkFillScratchState.Columns;

namespace Nethermind.State.Flat.History.Changesets;

public sealed class BulkFillSession : IDisposable, IWorldStateScopeProvider.ICodeDb
{
    private static ReadOnlySpan<byte> CheckpointKey => "replay-checkpoint"u8;
    private readonly IColumnsDb<Columns> _db;
    private readonly IDb _sourceCode;
    private readonly BulkFillScratchState _import;
    private readonly bool _rlpWrappedSlots;
    private readonly Dictionary<ValueHash256, byte[]> _pendingCode = [];
    private IColumnsWriteBatch<Columns>? _batch;
    private BlockHeader? _pendingBlock;
    private bool _hasFinalState;
    private bool _isFaulted;
    private bool _isDisposed;

    public BulkFillSession(IDbFactory factory, IDb sourceCode, Hash256 identity, BlockHeader anchor, bool rlpWrappedSlots)
    {
        DbSettings settings = new("TransactionIndexScratch", Path.Combine("tx-index-bulk", identity.ToString(false)))
        {
            CanDeleteFolder = false,
            SkipMetricsTracking = true,
        };
        _db = factory.CreateColumnsDb<Columns>(settings);
        DirectoryPath = factory.GetFullDbPath(settings);
        _sourceCode = sourceCode;
        _rlpWrappedSlots = rlpWrappedSlots;
        try
        {
            _import = new BulkFillScratchState(_db, identity, (ulong)anchor.Number);
            CurrentState = new StateId(anchor);
            BlockHash = anchor.Hash ?? throw new ArgumentException("The scratch anchor must have a hash.", nameof(anchor));
            byte[]? checkpoint = _db.GetColumnDb(Columns.Metadata)[CheckpointKey];
            if (checkpoint is not null)
            {
                if (checkpoint.Length != sizeof(ulong) + 2 * Hash256.Size) throw new ScratchStateUnusableException("Invalid bulk replay checkpoint.");
                CurrentState = new StateId(BinaryPrimitives.ReadUInt64BigEndian(checkpoint), new ValueHash256(checkpoint.AsSpan(sizeof(ulong), Hash256.Size)));
                BlockHash = new Hash256(checkpoint.AsSpan(sizeof(ulong) + Hash256.Size));
                if (CurrentState.BlockNumber < anchor.Number) throw new ScratchStateUnusableException("Bulk replay checkpoint precedes its anchor.");
                IsReady = true;
            }
        }
        catch
        {
            _db.Dispose();
            throw;
        }
    }

    public StateId CurrentState { get; private set; }
    public Hash256 BlockHash { get; private set; }
    public bool IsReady { get; private set; }
    public string DirectoryPath { get; }
    public long Size => _db.GatherMetric().Size;

    /// <summary>Drops the replayed state once its coverage is published. The checkpoint goes first, so a release cut
    /// short by cancellation leaves no replay base behind; the next start finds coverage complete and releases again.</summary>
    public void ReleaseState(CancellationToken token)
    {
        RequireHealthy();
        if (_batch is not null) throw new InvalidOperationException("Cannot release an active replay block.");
        _db.GetColumnDb(Columns.Metadata).Remove(CheckpointKey);
        _db.SyncWal();
        IsReady = false;
        Span<byte> upper = stackalloc byte[65];
        upper.Fill(0xFF);
        foreach (Columns column in new[] { Columns.Accounts, Columns.Storage, Columns.Clears, Columns.Code, Columns.Metadata })
        {
            token.ThrowIfCancellationRequested();
            if (_db.GetColumnDb(column) is not IRangeRemovableKeyValueStore removable)
                throw new NotSupportedException("Scratch cleanup requires range deletion support.");
            removable.RemoveRange([], upper);
        }
        _db.SyncWal();
        foreach (Columns column in Enum.GetValues<Columns>())
        {
            token.ThrowIfCancellationRequested();
            ((IRangeRemovableKeyValueStore)_db.GetColumnDb(column)).ReclaimRange([0], upper);
        }
    }

    public bool ImportPage(ISortedKeyValueStore source, HistoryRowFormat format, FlatHistoryColumns column, CancellationToken token)
        => ReadImportPage(source, format, column, token).Complete;

    internal double ImportFraction(FlatHistoryColumns column) => _import.ImportFraction(column);

    internal HistoricalStateScan.Page ReadImportPage(ISortedKeyValueStore source, HistoryRowFormat format, FlatHistoryColumns column, CancellationToken token)
    {
        RequireHealthy();
        if (IsReady) throw new InvalidOperationException("Scratch replay has already started.");
        return _import.ImportPage(source, format, column, 8192, token);
    }

    public void VerifyAnchor(CancellationToken token)
    {
        RequireHealthy();
        if (IsReady) return;
        _import.VerifyAnchor(CurrentState.StateRoot.ToCommitment(), _rlpWrappedSlots, token);
        try
        {
            _db.GetColumnDb(Columns.Metadata).PutSpan(CheckpointKey, EncodeCheckpoint(CurrentState, BlockHash));
            _db.SyncWal();
            IsReady = true;
        }
        catch
        {
            _isFaulted = true;
            throw;
        }
    }

    public void ImportGenesis(IEnumerable<KeyValuePair<Address, Account>> allocations, CancellationToken token)
    {
        RequireHealthy();
        if (CurrentState.BlockNumber != 0 || _batch is not null)
            throw new InvalidOperationException("Genesis bootstrap requires a genesis scratch anchor.");
        if (IsReady) return;
        token.ThrowIfCancellationRequested();
        IColumnsWriteBatch<Columns> batch = _db.StartWriteBatch();
        try
        {
            IWriteBatch accounts = batch.GetColumnBatch(Columns.Accounts);
            foreach ((Address address, Account account) in allocations)
            {
                token.ThrowIfCancellationRequested();
                if (account.StorageRoot != Keccak.EmptyTreeHash || account.CodeHash != Keccak.OfAnEmptyString)
                    throw new NotSupportedException("Genesis bootstrap requires plain account allocations.");
                accounts.PutSpan(address.ToAccountPath.Bytes, AccountDecoder.Slim.EncodeAsBytes(account));
            }
            IWriteBatch metadata = batch.GetColumnBatch(Columns.Metadata);
            foreach (Columns column in new[] { Columns.Accounts, Columns.Storage, Columns.Clears })
                metadata.PutSpan(new byte[] { (byte)column }, new byte[] { 1 });
            token.ThrowIfCancellationRequested();
        }
        catch
        {
            _isFaulted = true;
            try
            {
                batch.Clear();
            }
            finally
            {
                batch.Dispose();
            }
            throw;
        }
        try
        {
            batch.Dispose();
            _db.SyncWal();
        }
        catch
        {
            _isFaulted = true;
            throw;
        }
        VerifyAnchor(token);
    }

    public void BeginBlock(BlockHeader block)
    {
        RequireHealthy();
        if (!IsReady || _batch is not null || block.Number != CurrentState.BlockNumber + 1 || block.ParentHash != BlockHash || block.Hash is null)
            throw new InvalidOperationException("Bulk replay must continue its durable canonical checkpoint.");
        _batch = _db.StartWriteBatch();
        _pendingBlock = block;
        _hasFinalState = false;
    }

    internal BulkFillStateReader CreateReader() => new(_db, CurrentState, _rlpWrappedSlots);

    internal void StageFinalState(Func<IWorldStateScopeProvider.IBlockChangeSnapshot> takeSnapshot)
    {
        RequireHealthy();
        if (_batch is null || _pendingBlock is null || _hasFinalState) throw new InvalidOperationException("Unexpected bulk final-state capture.");
        using IWorldStateScopeProvider.IBlockChangeSnapshot snapshot = takeSnapshot();
        using BulkFillStateWriter writer = new(_db, _batch, (ulong)_pendingBlock.Number, _rlpWrappedSlots);
        snapshot.WriteTo(writer);
        _hasFinalState = true;
    }

    public void CommitBlock()
    {
        RequireHealthy();
        if (!_hasFinalState || _batch is null || _pendingBlock?.Hash is null) throw new InvalidOperationException("Bulk replay has no complete final state.");
        StateId next = new(_pendingBlock);
        Hash256 hash = _pendingBlock.Hash;
        try
        {
            _batch.GetColumnBatch(Columns.Metadata).PutSpan(CheckpointKey, EncodeCheckpoint(next, hash));
            IColumnsWriteBatch<Columns> batch = _batch;
            _batch = null;
            batch.Dispose();
            _db.SyncWal();
            CurrentState = next;
            BlockHash = hash;
            _pendingBlock = null;
            _pendingCode.Clear();
        }
        catch
        {
            _isFaulted = true;
            throw;
        }
    }

    public byte[]? GetCode(in ValueHash256 codeHash) =>
        codeHash == ValueKeccak.OfAnEmptyString ? [] :
        _pendingCode.TryGetValue(codeHash, out byte[]? code) ? code :
        _db.GetColumnDb(Columns.Code)[codeHash.Bytes] ?? _sourceCode[codeHash.Bytes]
        ?? throw new InvalidDataException($"Missing code {codeHash} during bulk replay.");

    public IWorldStateScopeProvider.ICodeSetter BeginCodeWrite()
    {
        RequireHealthy();
        if (_batch is null) throw new InvalidOperationException("Code writes require an active bulk replay block.");
        return new CodeWriter(this);
    }

    public void CleanStorage(CancellationToken token)
    {
        RequireHealthy();
        if (_batch is not null) throw new InvalidOperationException("Cannot clean an active replay block.");
        BulkFillStorageCleanup.Run(_db, token);
    }

    private static byte[] EncodeCheckpoint(in StateId state, Hash256 hash)
    {
        byte[] bytes = new byte[sizeof(ulong) + 2 * Hash256.Size];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, state.BlockNumber);
        state.StateRoot.Bytes.CopyTo(bytes.AsSpan(sizeof(ulong)));
        hash.Bytes.CopyTo(bytes.AsSpan(sizeof(ulong) + Hash256.Size));
        return bytes;
    }

    private void RequireHealthy()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (_isFaulted) throw new InvalidOperationException("Reopen the bulk replay after a failed write.");
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        try
        {
            if (_batch is not null)
            {
                try
                {
                    _batch.Clear();
                }
                finally
                {
                    _batch.Dispose();
                }
            }
        }
        finally
        {
            _db.Dispose();
        }
    }

    private sealed class CodeWriter(BulkFillSession session) : IWorldStateScopeProvider.ICodeSetter
    {
        public void Set(in ValueHash256 codeHash, ReadOnlySpan<byte> code)
        {
            session.RequireHealthy();
            if (ValueKeccak.Compute(code) != codeHash) throw new InvalidDataException("Invalid bulk replay code hash.");
            byte[] bytes = code.ToArray();
            session._pendingCode[codeHash] = bytes;
            session._batch!.GetColumnBatch(Columns.Code).Set(codeHash.Bytes, bytes);
        }
        public void Dispose() { }
    }
}
