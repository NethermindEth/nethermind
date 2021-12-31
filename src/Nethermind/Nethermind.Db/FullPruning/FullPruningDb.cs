//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

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
    public class FullPruningDb : IDb, IFullPruningDb
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

        public byte[]? this[byte[] key]
        {
            get => _currentDb[key]; // we are reading from the main DB
            set
            {
                _currentDb[key] = value; // we are writing to the main DB
                IDb? cloningDb = _pruningContext?.CloningDb;
                if (cloningDb != null) // if pruning is in progress we are also writing to the secondary, copied DB
                {
                    cloningDb[key] = value;
                    _updateDuplicateWriteMetrics?.Invoke();
                }
            }
        }

        // we also need to duplicate writes that are in batches
        public IBatch StartBatch() => 
            new DuplicatingBatch(
                _currentDb.StartBatch(), 
                _pruningContext?.CloningDb.StartBatch(),
                _updateDuplicateWriteMetrics);

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
        public void Remove(byte[] key)
        {
            _currentDb.Remove(key);
            IDb? cloningDb = _pruningContext?.CloningDb;
            cloningDb?.Remove(key);
        }

        public bool KeyExists(byte[] key) => _currentDb.KeyExists(key);

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

        /// <inheritdoc />
        public virtual bool TryStartPruning(out IPruningContext context)
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
            PruningContext newContext = new(this, CreateDb(ClonedDbSettings()), _updateDuplicateWriteMetrics);
            PruningContext? pruningContext = Interlocked.CompareExchange(ref _pruningContext, newContext, null);
            context = pruningContext ?? newContext;
            if (pruningContext is null)
            {
                PruningStarted?.Invoke(this, EventArgs.Empty);
                return true;
            }

            return false;
        }
        
        /// <inheritdoc />
        public string GetPath(string basePath) => _settings.DbPath.GetApplicationResourcePath(basePath);
        
        /// <inheritdoc />
        public string InnerDbName => _currentDb.Name;

        public event EventHandler? PruningStarted;
        public event EventHandler? PruningFinished;

        private void FinishPruning()
        {
            IDb oldDb = Interlocked.Exchange(ref _currentDb, _pruningContext?.CloningDb);
            Task.Run(() => ClearOldDb(oldDb));
        }

        protected virtual void ClearOldDb(IDb oldDb)
        {
            oldDb.Clear();
        }

        private void CancelPruning(PruningContext pruningContext)
        {
            PruningFinished?.Invoke(this, EventArgs.Empty);
            Interlocked.CompareExchange(ref _pruningContext, null, pruningContext);
        }

        private class PruningContext : IPruningContext
        {
            private bool _committed = false;
            public IDb CloningDb { get; }
            private readonly FullPruningDb _db;
            private readonly Action? _updateDuplicateWriteMetrics;

            public PruningContext(FullPruningDb db, IDb cloningDb, Action? updateDuplicateWriteMetrics)
            {
                CloningDb = cloningDb;
                _db = db;
                _updateDuplicateWriteMetrics = updateDuplicateWriteMetrics;
            }

            /// <inheritdoc />
            public byte[]? this[byte[] key]
            {
                set
                {
                    CloningDb[key] = value;
                    _updateDuplicateWriteMetrics?.Invoke();
                }
            }

            /// <inheritdoc />
            public void Commit()
            {
                _db.FinishPruning();
                _committed = true; // we mark the context as committed.
            }

            /// <inheritdoc />
            public void MarkStart()
            {
                Metrics.StateDbPruning = 1;
            }

            /// <inheritdoc />
            public void Dispose()
            {
                _db.CancelPruning(this);
                if (!_committed)
                {
                    // if the context was not committed, then pruning failed and we delete the cloned DB
                    CloningDb.Clear();
                }
                Metrics.StateDbPruning = 0;
            }
        }
        
        /// <summary>
        /// Batch that duplicates writes to the current DB and the cloned DB batches.
        /// </summary>
        private class DuplicatingBatch : IBatch
        {
            private readonly IBatch _batch;
            private readonly IBatch? _clonedBatch;
            private readonly Action? _updateDuplicateWriteMetrics;

            public DuplicatingBatch(
                IBatch batch, 
                IBatch? clonedBatch, 
                Action? updateDuplicateWriteMetrics)
            {
                _batch = batch;
                _clonedBatch = clonedBatch;
                _updateDuplicateWriteMetrics = updateDuplicateWriteMetrics;
            }

            public void Dispose()
            {
                _batch.Dispose();
                _clonedBatch?.Dispose();
            }

            public byte[]? this[byte[] key]
            {
                get => _batch[key];
                set
                {
                    _batch[key] = value;
                    if (_clonedBatch is not null)
                    {
                        _clonedBatch[key] = value;
                        _updateDuplicateWriteMetrics?.Invoke();
                    }
                }
            }
        }
    }
}
