// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
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
    /// It uses <see cref="IRocksDbFactory"/> to create new pruning DB's. Check <see cref="FullPruningInnerDbFactory"/> to see how inner sub DB's are organised.
    /// </remarks>
    public class FullPruningDb : IDb, IFullPruningDb, ITunableDb
    {
        private readonly RocksDbSettings _settings;
        private readonly IRocksDbFactory _dbFactory;
        private readonly Action? _updateDuplicateWriteMetrics;

        // current main DB, will be written to and will be main source for reading
        private IDb _currentDb;

        // current pruning context, secondary DB that the state will be written to, as well as state trie will be copied to
        // this will be null if no full pruning is in progress
        private PruningContext? _pruningContext;

        public FullPruningDb(RocksDbSettings settings, IRocksDbFactory dbFactory, Action? updateDuplicateWriteMetrics = null)
        {
            _settings = settings;
            _dbFactory = dbFactory;
            _updateDuplicateWriteMetrics = updateDuplicateWriteMetrics;
            _currentDb = CreateDb(_settings);
        }

        private IDb CreateDb(RocksDbSettings settings) => _dbFactory.CreateDb(settings);

        public byte[]? this[ReadOnlySpan<byte> key]
        {
            get
            {
                byte[]? value = _currentDb[key]; // we are reading from the main DB
                if (_pruningContext?.DuplicateReads == true)
                {
                    Duplicate(_pruningContext.CloningDb, key, value);
                }

                return value;
            }
            set
            {
                _currentDb[key] = value; // we are writing to the main DB
                IDb? cloningDb = _pruningContext?.CloningDb;
                if (cloningDb is not null) // if pruning is in progress we are also writing to the secondary, copied DB
                {
                    Duplicate(cloningDb, key, value);
                }
            }
        }

        public byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
        {
            byte[]? value = _currentDb.Get(key, flags); // we are reading from the main DB
            if (_pruningContext?.DuplicateReads == true)
            {
                Duplicate(_pruningContext.CloningDb, key, value);
            }

            return value;
        }

        private void Duplicate(IKeyValueStore db, ReadOnlySpan<byte> key, byte[]? value)
        {
            db[key] = value;
            _updateDuplicateWriteMetrics?.Invoke();
        }

        // we also need to duplicate writes that are in batches
        public IBatch StartBatch() =>
            _pruningContext is null
                ? _currentDb.StartBatch()
                : new DuplicatingBatch(_currentDb.StartBatch(), _pruningContext.CloningDb.StartBatch(), this);

        public void Dispose()
        {
            _currentDb.Dispose();
            _pruningContext?.CloningDb.Dispose();
        }

        public string Name => _settings.DbName;

        public KeyValuePair<byte[], byte[]?>[] this[byte[][] keys] => _currentDb[keys];

        public IEnumerable<KeyValuePair<byte[], byte[]>> GetAll(bool ordered = false) => _currentDb.GetAll(ordered);

        public IEnumerable<byte[]> GetAllValues(bool ordered = false) => _currentDb.GetAllValues(ordered);

        // we need to remove from both DB's
        public void Remove(ReadOnlySpan<byte> key)
        {
            _currentDb.Remove(key);
            IDb? cloningDb = _pruningContext?.CloningDb;
            cloningDb?.Remove(key);
        }

        public bool KeyExists(ReadOnlySpan<byte> key) => _currentDb.KeyExists(key);

        // inner DB's can be deleted in the future and
        // we cannot expose a DB that will potentially be later deleted
        public IDb Innermost => this;

        // we need to flush both DB's
        public void Flush()
        {
            _currentDb.Flush();
            IDb? cloningDb = _pruningContext?.CloningDb;
            cloningDb?.Flush();
        }

        // we need to clear both DB's
        public void Clear()
        {
            _currentDb.Clear();
            IDb? cloningDb = _pruningContext?.CloningDb;
            cloningDb?.Clear();
        }

        /// <inheritdoc />
        public bool CanStartPruning => _pruningContext is null; // we can start pruning only if no pruning is in progress

        public bool TryStartPruning(out IPruningContext context) => TryStartPruning(true, out context);

        /// <inheritdoc />
        public virtual bool TryStartPruning(bool duplicateReads, out IPruningContext context)
        {
            RocksDbSettings ClonedDbSettings()
            {
                RocksDbSettings clonedDbSettings = _settings.Clone();
                clonedDbSettings.DeleteOnStart = true;
                return clonedDbSettings;
            }

            // create new pruning context with new sub DB and try setting it as current
            // returns true when new pruning is started
            // returns false only on multithreaded access, returns started pruning context then
            PruningContext newContext = new(this, CreateDb(ClonedDbSettings()), duplicateReads);
            PruningContext? pruningContext = Interlocked.CompareExchange(ref _pruningContext, newContext, null);
            context = pruningContext ?? newContext;
            if (pruningContext is null)
            {
                PruningStarted?.Invoke(this, new PruningEventArgs(context, true));
                return true;
            }

            return false;
        }

        /// <inheritdoc />
        public string GetPath(string basePath) => _settings.DbPath.GetApplicationResourcePath(basePath);

        /// <inheritdoc />
        public string InnerDbName => _currentDb.Name;

        public event EventHandler<PruningEventArgs>? PruningStarted;
        public event EventHandler<PruningEventArgs>? PruningFinished;

        private void FinishPruning()
        {
            IDb oldDb = Interlocked.Exchange(ref _currentDb, _pruningContext?.CloningDb);
            ClearOldDb(oldDb);
        }

        protected virtual void ClearOldDb(IDb oldDb)
        {
            oldDb.Clear();
        }

        private void FinishPruning(PruningContext pruningContext, bool success)
        {
            PruningFinished?.Invoke(this, new PruningEventArgs(pruningContext, success));
            Interlocked.CompareExchange(ref _pruningContext, null, pruningContext);
        }

        private class PruningContext : IPruningContext
        {
            private bool _committed = false;
            private bool _disposed = false;
            public IDb CloningDb { get; }
            public bool DuplicateReads { get; }
            private readonly FullPruningDb _db;

            private long _counter = 0;
            private ConcurrentQueue<IBatch> _batches = new();

            public PruningContext(FullPruningDb db, IDb cloningDb, bool duplicateReads)
            {
                CloningDb = cloningDb;
                DuplicateReads = duplicateReads;
                _db = db;
            }

            /// <inheritdoc />
            public byte[]? this[ReadOnlySpan<byte> key]
            {
                get => CloningDb[key];
                set
                {
                    if (!_batches.TryDequeue(out IBatch currentBatch))
                    {
                        currentBatch = CloningDb.StartBatch();
                    }

                    _db.Duplicate(currentBatch, key, value);
                    long val = Interlocked.Increment(ref _counter);
                    if (val % 10000 == 0)
                    {
                        currentBatch.Dispose();
                    }
                    else
                    {
                        _batches.Enqueue(currentBatch);
                    }
                }
            }

            /// <inheritdoc />
            public void Commit()
            {
                foreach (IBatch batch in _batches)
                {
                    batch.Dispose();
                }

                _db.FinishPruning();
                _committed = true; // we mark the context as committed.
            }

            /// <inheritdoc />
            public void MarkStart()
            {
                Metrics.StateDbPruning = 1;
            }

            public CancellationTokenSource CancellationTokenSource { get; } = new();

            /// <inheritdoc />
            public void Dispose()
            {
                if (!_disposed)
                {
                    _db.FinishPruning(this, _committed);
                    if (!_committed)
                    {
                        // if the context was not committed, then pruning failed and we delete the cloned DB
                        CloningDb.Clear();
                    }

                    CancellationTokenSource.Dispose();
                    Metrics.StateDbPruning = 0;
                    _disposed = true;
                }
            }
        }

        /// <summary>
        /// Batch that duplicates writes to the current DB and the cloned DB batches.
        /// </summary>
        private class DuplicatingBatch : IBatch
        {
            private readonly IBatch _batch;
            private readonly IBatch _clonedBatch;
            private readonly FullPruningDb _db;

            public DuplicatingBatch(
                IBatch batch,
                IBatch clonedBatch,
                FullPruningDb db)
            {
                _batch = batch;
                _clonedBatch = clonedBatch;
                _db = db;
            }

            public void Dispose()
            {
                _batch.Dispose();
                _clonedBatch.Dispose();
            }

            public byte[]? this[ReadOnlySpan<byte> key]
            {
                get => _batch[key];
                set
                {
                    _batch[key] = value;
                    _db.Duplicate(_clonedBatch, key, value);
                }
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
