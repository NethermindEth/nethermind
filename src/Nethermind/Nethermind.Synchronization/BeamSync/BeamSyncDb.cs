//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.Synchronization.BeamSync
{
    public class BeamSyncDb : SyncFeed<StateSyncBatch>, IDb
    {
        public UInt256 RequiredPeerDifficulty { get; private set; } = UInt256.Zero;

        /// <summary>
        /// The actual state DB that can be used for reading the fast synced state from. Also used for writes of any
        /// nodes without dependencies.
        /// </summary>
        private IDb _stateDb;

        /// <summary>
        /// This DB stands in front of the state DB for reads and serves as beam sync DB write-to DB for any writes
        /// that are not final (do not have any unfilled child nodes).
        /// </summary>
        private IDb _tempDb;

        private ILogger _logger;

        public BeamSyncDb(IDb stateDb, IDb tempDb, ILogManager logManager)
        {
            _logger = logManager.GetClassLogger<BeamSyncDb>();
            _stateDb = stateDb ?? throw new ArgumentNullException(nameof(stateDb));
            _tempDb = tempDb ?? throw new ArgumentNullException(nameof(tempDb));
        }

        private bool _isDisposed = false;

        public void Dispose()
        {
            _isDisposed = true;
            _tempDb.Dispose();
        }

        public string Name => _tempDb.Name;

        private int _resolvedKeysCount;

        private object _diffLock = new object();

        private HashSet<Keccak> _requestedNodes = new HashSet<Keccak>();

        private TimeSpan _contextExpiryTimeSpan = TimeSpan.FromSeconds(4);
        private TimeSpan _preProcessExpiryTimeSpan = TimeSpan.FromSeconds(15);

        public byte[] this[byte[] key]
        {
            get
            {
                lock (_diffLock)
                {
                    RequiredPeerDifficulty = UInt256.Max(RequiredPeerDifficulty, BeamSyncContext.MinimumDifficulty.Value);
                }

                // it is not possible for the item to be requested from the DB and missing in the DB unless the DB is corrupted
                // if it is missing in the MemDb then it must exist somewhere on the web (unless the block is corrupted / invalid)

                // we grab the node from the web through requests

                // if the block is invalid then we will be timing out for a long time
                // in such case it would be good to have some gossip about corrupted blocks
                // but such gossip would be cheap
                // if we keep timing out then we would finally reject the block (but only shelve it instead of marking invalid)

                bool wasInDb = true;
                var fromMem = _stateDb[key];
                while (true)
                {
                    if (BeamSyncContext.Cancelled.Value.IsCancellationRequested)
                    {
                        throw new TaskCanceledException("Beam Sync task cancelled by a new block.");
                    }

                    if (_isDisposed)
                    {
                        throw new ObjectDisposedException("Beam Sync DB disposed");
                    }

                    // shall I leave test logic forever?
                    if (BeamSyncContext.LoopIterationsToFailInTest.Value != null)
                    {
                        int? currentValue = BeamSyncContext.LoopIterationsToFailInTest.Value--;
                        if (currentValue == 0)
                        {
                            throw new Exception();
                        }
                    }

                    fromMem ??= _tempDb[key] ?? _stateDb[key];
                    if (fromMem == null)
                    {
                        if (Bytes.AreEqual(key, Keccak.Zero.Bytes))
                        {
                            // we store sync progress data at Keccak.Zero;
                            return null;
                        }

                        TimeSpan expiry = _contextExpiryTimeSpan;
                        if (BeamSyncContext.Description.Value?.Contains("preProcess") ?? false)
                        {
                            expiry = _preProcessExpiryTimeSpan;
                        }

                        if (DateTime.UtcNow - (BeamSyncContext.LastFetchUtc.Value ?? DateTime.UtcNow) > expiry)
                        {
                            string message = $"Beam sync request {BeamSyncContext.Description.Value} with last update on {BeamSyncContext.LastFetchUtc.Value:hh:mm:ss.fff} has expired";
                            if (_logger.IsDebug) _logger.Debug(message);
                            throw new BeamSyncException(message);
                        }

                        wasInDb = false;
                        // _logger.Info($"BEAM SYNC Asking for {key.ToHexString()} - resolved keys so far {_resolvedKeysCount}");

                        int count;
                        lock (_requestedNodes)
                        {
                            _requestedNodes.Add(new Keccak(key));
                            count = _requestedNodes.Count;
                        }

                        // _logger.Error($"Requested {key.ToHexString()}");

                        Activate();
                        _autoReset.WaitOne(50);
                    }
                    else
                    {
                        if (!wasInDb)
                        {
                            BeamSyncContext.ResolvedInContext.Value++;
                            Interlocked.Increment(ref _resolvedKeysCount);
                            // if (_logger.IsInfo) _logger.Info($"{_description} Resolved key {key.ToHexString()} of context {BeamSyncContext.Description.Value} - resolved ctx {BeamSyncContext.ResolvedInContext.Value} | total {_resolvedKeysCount}");
                        }

                        BeamSyncContext.LastFetchUtc.Value = DateTime.UtcNow;
                        
                        return fromMem;
                    }
                }
            }

            set => _tempDb[key] = value;
        }

        public KeyValuePair<byte[], byte[]>[] this[byte[][] keys] => keys.Select(k => new KeyValuePair<byte[], byte[]>(k, this[k])).ToArray();

        public IEnumerable<KeyValuePair<byte[], byte[]>> GetAll(bool ordered = false)
        {
            throw new NotSupportedException();
        }

        public IEnumerable<byte[]> GetAllValues(bool ordered = false)
        {
            throw new NotSupportedException();
        }

        public void StartBatch()
        {
        }

        public void CommitBatch()
        {
        }

        public void Remove(byte[] key)
        {
            _tempDb.Remove(key);
        }

        public bool KeyExists(byte[] key)
        {
            return _tempDb.KeyExists(key);
        }

        public IDb Innermost => _tempDb.Innermost;

        public void Flush()
        {
            _tempDb.Flush();
        }

        public void Clear()
        {
            _tempDb.Clear();
        }

        public override Task<StateSyncBatch> PrepareRequest()
        {
            StateSyncBatch request;
            lock (_requestedNodes)
            {
                if (_requestedNodes.Count == 0)
                {
                    return Task.FromResult((StateSyncBatch)null);
                }

                request = new StateSyncBatch();
                request.ConsumerId = FeedId;

                if (_requestedNodes.Count < 256)
                {
                    // do not make it state sync item :)
                    request.RequestedNodes = _requestedNodes.Select(n => new StateSyncItem(n, NodeDataType.State, 0, 0)).ToArray();
                    _requestedNodes.Clear();
                }
                else
                {
                    Keccak[] source = _requestedNodes.ToArray();
                    request.RequestedNodes = new StateSyncItem[256];
                    _requestedNodes.Clear();
                    for (int i = 0; i < source.Length; i++)
                    {
                        if (i < 256)
                        {
                            // not state sync item
                            request.RequestedNodes[i] = new StateSyncItem(source[i], NodeDataType.State, 0, 0);
                        }
                        else
                        {
                            _requestedNodes.Add(source[i]);
                        }
                    }
                }
            }

            return Task.FromResult(request);
        }

        public override SyncResponseHandlingResult HandleResponse(StateSyncBatch stateSyncBatch)
        {
            if (stateSyncBatch.ConsumerId != FeedId)
            {
                return SyncResponseHandlingResult.InvalidFormat;
            }

            int consumed = 0;

            byte[][] data = stateSyncBatch.Responses;
            if (data != null)
            {
                for (int i = 0; i < data.Length; i++)
                {
                    Keccak key = stateSyncBatch.RequestedNodes[i].Hash;
                    if (data.Length > i && data[i] != null)
                    {
                        if (Keccak.Compute(data[i]) == key)
                        {
                            _tempDb[key.Bytes] = data[i];
                            // _requestedNodes.Remove(hashes[i], out _);
                            consumed++;
                        }
                        else
                        {
                            if (_logger.IsWarn) _logger.Warn("Received a node data which does not match hash.");
                        }
                    }
                    else
                    {
                        if (_tempDb[key.Bytes] == null)
                        {
                            lock (_requestedNodes)
                            {
                                _requestedNodes.Add(key);
                            }
                        }
                    }
                }
            }

            _autoReset.Set();
            return consumed == 0 ? SyncResponseHandlingResult.NoData : SyncResponseHandlingResult.OK;
        }

        private AutoResetEvent _autoReset = new AutoResetEvent(true);
        
        public override bool IsMultiFeed => false;
    }
}