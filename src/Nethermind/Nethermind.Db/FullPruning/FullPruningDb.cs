// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.Db.FullPruning
{
    /// <summary>
    /// Database facade that allows full pruning.
    /// </summary>
    /// <remarks>
    /// Allows to start pruning with <see cref="TryStartPruning"/> in a thread safe way.
    /// When pruning is started it duplicates all writes to current DB as well as the new one for full pruning, this includes write batches.
    /// When <see cref="IPruningContext"/> returned in <see cref="TryStartPruning"/> is <see cref="IDisposable.Dispose"/>d it will delete the pruning DB if the pruning was not successful.
    /// It uses <see cref="IRocksDbFactory"/> to create new pruning DB's. Check <see cref="FullPruningInnerDbFactory"/> to see how inner sub DB's are organized.
    /// </remarks>
    public class FullPruningDb : IDb, IFullPruningDb, ITunableDb
    {
        private readonly DbSettings _settings;
        private readonly IDbFactory _dbFactory;
        private readonly Action? _updateDuplicateWriteMetrics;

        // current main DB, will be written to and will be main source for reading
        private IDb _currentDb;

        // current pruning context, secondary DB that the state will be written to, as well as state trie will be copied to
        // this will be null if no full pruning is in progress
        private volatile PruningContext? _pruningContext;
        private Lock _startLock = new();
        private int _activeAtomicBatches;

        public FullPruningDb(DbSettings settings, IDbFactory dbFactory, Action? updateDuplicateWriteMetrics = null)
        {
            _settings = settings;
            _dbFactory = dbFactory;
            _updateDuplicateWriteMetrics = updateDuplicateWriteMetrics;
            _currentDb = CreateDb(_settings);
        }

        private IDb CreateDb(DbSettings settings) => _dbFactory.CreateDb(settings);

        public byte[]? this[ReadOnlySpan<byte> key]
        {
            get => Get(key, ReadFlags.None);
            set => Set(key, value, WriteFlags.None);
        }

        public byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
        {
            byte[]? value = _currentDb.Get(key, flags); // we are reading from the main DB
            PruningContext? pruningContext = _pruningContext;
            if (value is not null && pruningContext?.DuplicateReads == true && (flags & ReadFlags.SkipDuplicateRead) == 0)
            {
                Duplicate(pruningContext.CloningDb, key, value, WriteFlags.None);
            }

            return value;
        }

        public Span<byte> GetSpan(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
        {
            Span<byte> value = _currentDb.GetSpan(key, flags); // we are reading from the main DB
            PruningContext? pruningContext = _pruningContext;
            if (!value.IsNull() && pruningContext?.DuplicateReads == true && (flags & ReadFlags.SkipDuplicateRead) == 0)
            {
                Duplicate(pruningContext.CloningDb, key, value, WriteFlags.None);
            }

            return value;
        }

        public MemoryManager<byte>? GetOwnedMemory(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
        {
            MemoryManager<byte>? memoryManager = _currentDb.GetOwnedMemory(key, flags);
            PruningContext? pruningContext = _pruningContext;
            if (memoryManager is not null && pruningContext?.DuplicateReads == true && (flags & ReadFlags.SkipDuplicateRead) == 0)
            {
                Duplicate(pruningContext.CloningDb, key, memoryManager.GetSpan(), WriteFlags.None);
            }

            return memoryManager;
        }

        public bool TryGet<TArg>(scoped ReadOnlySpan<byte> key, ReadFlags flags, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct
        {
            PruningContext? pruningContext = _pruningContext;
            if (pruningContext?.DuplicateReads != true || (flags & ReadFlags.SkipDuplicateRead) != 0)
            {
                return _currentDb.TryGet(key, flags, action, arg); // we are reading from the main DB
            }

            // Copies rather than wrapping the callback: passing a TArg-wrapping type to the interface's TryGet is generic recursion
            // through a Nethermind.Core parameter, which bflat's ILC fails to compile in the zkVM guests.
            byte[]? value = Get(key, flags);
            if (value is null) return false;

            action(value, arg);
            return true;
        }

        public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
        {
            _currentDb.Set(key, value, flags); // we are writing to the main DB
            IDb? cloningDb = _pruningContext?.CloningDb;
            if (cloningDb is not null) // if pruning is in progress we are also writing to the secondary, copied DB
            {
                Duplicate(cloningDb, key, value, flags);
            }
        }

        public void PutSpan(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None)
        {
            _currentDb.PutSpan(key, value, flags); // we are writing to the main DB
            IDb? cloningDb = _pruningContext?.CloningDb;
            if (cloningDb is not null) // if pruning is in progress we are also writing to the secondary, copied DB
            {
                Duplicate(cloningDb, key, value, flags);
            }
        }

        private void Duplicate(IWriteOnlyKeyValueStore db, ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags)
        {
            db.Set(key, value, flags);
            _updateDuplicateWriteMetrics?.Invoke();
        }

        private void Duplicate(IWriteOnlyKeyValueStore db, ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags)
        {
            db.PutSpan(key, value, flags);
            _updateDuplicateWriteMetrics?.Invoke();
        }

        private void DuplicateMerge(IMergeableKeyValueStore db, ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags)
        {
            db.Merge(key, value, flags);
            _updateDuplicateWriteMetrics?.Invoke();
        }

        // we also need to duplicate writes that are in batches
        public IWriteBatch StartWriteBatch()
        {
            IDb? cloningDb = _pruningContext?.CloningDb;
            return cloningDb is null
                ? _currentDb.StartWriteBatch()
                : new DuplicatingWriteBatch(_currentDb.StartWriteBatch(), cloningDb.StartWriteBatch(), this);
        }

        /// <inheritdoc/>
        public IAtomicWriteBatch StartAtomicWriteBatch()
        {
            using Lock.Scope _ = _startLock.EnterScope();
            IAtomicWriteBatch batch = _currentDb.StartAtomicWriteBatch();
            AtomicWriteBatch wrapper;
            try
            {
                wrapper = new AtomicWriteBatch(this, batch);
            }
            catch
            {
                batch.Dispose();
                throw;
            }
            _pruningContext?.Invalidate();
            _activeAtomicBatches++;
            return wrapper;
        }

        private sealed class AtomicWriteBatch(FullPruningDb db, IAtomicWriteBatch inner) : IAtomicWriteBatch
        {
            private bool _disposed;

            public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None) => inner.Set(key, value, flags);
            public void PutSpan(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None) => inner.PutSpan(key, value, flags);
            public void Merge(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None) => inner.Merge(key, value, flags);
            public void Clear() => inner.Clear();

            public void Commit()
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                try
                {
                    inner.Commit();
                }
                finally
                {
                    Dispose();
                }
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                try
                {
                    inner.Dispose();
                }
                finally
                {
                    using Lock.Scope _ = db._startLock.EnterScope();
                    db._activeAtomicBatches--;
                }
            }
        }

        public void Dispose()
        {
            _currentDb.Dispose();
            _pruningContext?.CloningDb.Dispose();
        }

        public IDbMeta.DbMetric GatherMetric() => _currentDb.GatherMetric();

        public string Name => _settings.DbName;

        public IEnumerable<KeyValuePair<byte[], byte[]>> GetAll(bool ordered = false) => _currentDb.GetAll(ordered);

        public IEnumerable<byte[]> GetAllKeys(bool ordered = false) => _currentDb.GetAllKeys(ordered);

        public IEnumerable<byte[]> GetAllValues(bool ordered = false) => _currentDb.GetAllValues(ordered);

        // we need to remove from both DB's
        public void Remove(ReadOnlySpan<byte> key)
        {
            _currentDb.Remove(key);
            IDb? cloningDb = _pruningContext?.CloningDb;
            cloningDb?.Remove(key);
        }

        public bool KeyExists(ReadOnlySpan<byte> key) => _currentDb.KeyExists(key);

        public void DangerousReleaseMemory(in ReadOnlySpan<byte> span) => _currentDb.DangerousReleaseMemory(span);

        // inner DB's can be deleted in the future and
        // we cannot expose a DB that will potentially be later deleted
        public IDb Innermost => this;

        // we need to flush both DB's
        public void Flush(bool onlyWal)
        {
            _currentDb.Flush(onlyWal);
            IDb? cloningDb = _pruningContext?.CloningDb;
            cloningDb?.Flush(onlyWal);
        }

        // we need to clear both DB's
        public void Clear()
        {
            _currentDb.Clear();
            IDb? cloningDb = _pruningContext?.CloningDb;
            cloningDb?.Clear();
        }

        /// <inheritdoc />
        public bool CanStartPruning => _pruningContext is null && Volatile.Read(ref _activeAtomicBatches) == 0;

        public bool TryStartPruning([NotNullWhen(true)] out IPruningContext? context) => TryStartPruning(true, out context);

        /// <inheritdoc />
        public virtual bool TryStartPruning(bool duplicateReads, [NotNullWhen(true)] out IPruningContext? context)
        {
            DbSettings ClonedDbSettings()
            {
                DbSettings clonedDbSettings = _settings.Clone();
                clonedDbSettings.DeleteOnStart = true;
                return clonedDbSettings;
            }

            lock (_startLock)
            {
                if (!CanStartPruning)
                {
                    context = null;
                    return false;
                }
                context = _pruningContext = new(this, CreateDb(ClonedDbSettings()), duplicateReads);
            }
            PruningStarted?.Invoke(this, new PruningEventArgs(context, true));
            return true;
        }

        /// <inheritdoc />
        public string GetPath(string basePath) => _settings.DbPath.GetApplicationResourcePath(basePath);

        /// <inheritdoc />
        public string InnerDbName => _currentDb.Name;

        public event EventHandler<PruningEventArgs>? PruningStarted;
        public event EventHandler<PruningEventArgs>? PruningFinished;

        protected virtual void ClearOldDb(IDb oldDb) => oldDb.Clear();

        private void FinishPruning(PruningContext pruningContext, bool success) =>
            PruningFinished?.Invoke(this, new PruningEventArgs(pruningContext, success));

        [DoesNotReturn, StackTraceHidden]
        private static void ThrowPruningSuperseded() =>
            throw new InvalidOperationException("Pruning was superseded by atomic state synchronization writes.");

        private class PruningContext(FullPruningDb db, IDb cloningDb, bool duplicateReads) : IPruningContext
        {
            private bool _committed = false;
            private bool _disposed = false;
            public IDb CloningDb { get; } = cloningDb;
            private readonly Lock _lifecycle = new();
            private bool _invalidated;

            internal void Invalidate()
            {
                if (_invalidated || _disposed || _committed) return;
                _invalidated = true;
                _ = CancellationTokenSource.CancelAsync();
            }
            public bool DuplicateReads { get; } = duplicateReads;
            private readonly FullPruningDb _db = db;

            public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None) => _db.Duplicate(CloningDb, key, value, flags);

            public IWriteBatch StartWriteBatch() => CloningDb.StartWriteBatch();

            public byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None) => CloningDb.Get(key, flags);

            /// <inheritdoc />
            public void Commit()
            {
                using Lock.Scope lifecycle = _lifecycle.EnterScope();
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_committed) ThrowAlreadyCommitted();
                lock (_db._startLock)
                    if (_invalidated) ThrowPruningSuperseded();
                CloningDb.Flush();
                IDb oldDb;
                lock (_db._startLock)
                {
                    if (_invalidated) ThrowPruningSuperseded();
                    oldDb = Interlocked.Exchange(ref _db._currentDb, CloningDb);
                    _committed = true;
                }
                _db.ClearOldDb(oldDb);
            }

            [DoesNotReturn, StackTraceHidden]
            private static void ThrowAlreadyCommitted() =>
                throw new InvalidOperationException("Pruning context was already committed.");

            /// <inheritdoc />
            public void MarkStart() => Metrics.StateDbPruning = 1;

            private CancellationTokenSource CancellationTokenSource { get; } = new();
            public CancellationToken Superseded => CancellationTokenSource.Token;

            /// <inheritdoc />
            public void Dispose()
            {
                bool notify = false;
                bool committed = false;
                try
                {
                    using Lock.Scope lifecycle = _lifecycle.EnterScope();
                    using Lock.Scope _ = _db._startLock.EnterScope();
                    if (_disposed) return;
                    _disposed = true;
                    committed = _committed;
                    notify = true;
                    Interlocked.CompareExchange(ref _db._pruningContext, null, this);
                    try
                    {
                        if (!committed) CloningDb.Clear();
                    }
                    finally
                    {
                        try
                        {
                            CancellationTokenSource.Dispose();
                        }
                        finally
                        {
                            Metrics.StateDbPruning = 0;
                        }
                    }
                }
                finally
                {
                    if (notify) _db.FinishPruning(this, committed);
                }
            }
        }

        /// <summary>
        /// Batch that duplicates writes to the current DB and the cloned DB batches.
        /// </summary>
        private class DuplicatingWriteBatch(
            IWriteBatch writeBatch,
            IWriteBatch clonedWriteBatch,
            FullPruningDb db) : IWriteBatch
        {
            private readonly IWriteBatch _writeBatch = writeBatch;
            private readonly IWriteBatch _clonedWriteBatch = clonedWriteBatch;
            private readonly FullPruningDb _db = db;

            public void Dispose()
            {
                _writeBatch.Dispose();
                _clonedWriteBatch.Dispose();
            }

            public void Clear()
            {
                _writeBatch.Clear();
                _clonedWriteBatch.Clear();
            }

            public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
            {
                _writeBatch.Set(key, value, flags);
                _db.Duplicate(_clonedWriteBatch, key, value, flags);
            }

            public void Merge(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None)
            {
                _writeBatch.Merge(key, value, flags);
                _db.DuplicateMerge(_clonedWriteBatch, key, value, flags);
            }
        }

        public void Tune(ITunableDb.TuneType type)
        {
            if (_currentDb is ITunableDb tunableDb)
            {
                tunableDb.Tune(type);
            }
        }
    }
}
