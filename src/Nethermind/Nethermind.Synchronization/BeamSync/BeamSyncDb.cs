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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.BeamSync
{
    public class BeamSyncDb : SyncFeed<StateSyncBatch?>, IDb
    {
        public UInt256 RequiredPeerDifficulty { get; private set; } = UInt256.Zero;

        /// <summary>
        /// The actual state DB that can be used for reading the fast synced state from. Also used for writes of any
        /// nodes without dependencies.
        /// </summary>
        private readonly IDb _stateDb;

        /// <summary>
        /// This DB stands in front of the state DB for reads and serves as beam sync DB write-to DB for any writes
        /// that are not final (do not have any unfilled child nodes).
        /// </summary>
        private readonly IDb _tempDb;

        private readonly ISyncModeSelector _syncModeSelector;

        private readonly ILogger _logger;

        private IDb _targetDbForSaves;

        public BeamSyncDb(
            IDb stateDb,
            IDb tempDb,
            ISyncModeSelector syncModeSelector,
            ILogManager logManager,
            int contextTimeout = 4,
            int preProcessTimeout = 15)
        {
            _logger = logManager.GetClassLogger<BeamSyncDb>();
            _stateDb = stateDb ?? throw new ArgumentNullException(nameof(stateDb));
            _tempDb = tempDb ?? throw new ArgumentNullException(nameof(tempDb));
            _syncModeSelector = syncModeSelector ?? throw new ArgumentNullException(nameof(syncModeSelector));
            _syncModeSelector.Preparing += SyncModeSelectorOnPreparing;
            _syncModeSelector.Changing += SyncModeSelectorOnChanging;
            _syncModeSelector.Changed += SyncModeSelectorOnChanged;

            _targetDbForSaves = _tempDb; // before transition to full we are saving to beam DB
            _contextExpiryTimeSpan = TimeSpan.FromSeconds(contextTimeout);
            _preProcessExpiryTimeSpan = TimeSpan.FromSeconds(preProcessTimeout);
        }

        private readonly object _finishLock = new();

        private void SyncModeSelectorOnChanged(object? sender, SyncModeChangedEventArgs e)
        {
            if (e.IsBeamSyncFinished())
            {
                // the beam processor either already switched or is about to switch to the full sync mode
                // we should be already switched to the new database
                lock (_finishLock)
                {
                    if (CurrentState != SyncFeedState.Finished)
                    {
                        // we do not want to finish this feed - instead we will keep using it in Beam Synced RPC requests
                        // Finish();
                        UnregisterHandlers();
                    }
                }
            }
        }

        private void SyncModeSelectorOnPreparing(object? sender, SyncModeChangedEventArgs e)
        {
            // do nothing, the beam processor is cancelling beam executors now and they may be still writing
        }

        private void SyncModeSelectorOnChanging(object? sender, SyncModeChangedEventArgs e)
        {
            // at this stage beam executors are already cancelled and they no longer save to beam DB
            // standard processor is for sure not started yet - it is waiting for us to replace the target
            if (e.IsBeamSyncFinished())
            {
                Interlocked.Exchange(ref _targetDbForSaves, _stateDb);
            }
        }

        private bool _isDisposed;

        public void Dispose()
        {
            _isDisposed = true;
            UnregisterHandlers();
            _stateDb.Dispose();
            _tempDb.Dispose();
        }

        private void UnregisterHandlers()
        {
            _syncModeSelector.Preparing -= SyncModeSelectorOnPreparing;
            _syncModeSelector.Changing -= SyncModeSelectorOnChanging;
            _syncModeSelector.Changed -= SyncModeSelectorOnChanged;
        }

        public string Name => _tempDb.Name;

        private readonly object _diffLock = new();

        private readonly HashSet<Keccak> _requestedNodes = new();

        private readonly TimeSpan _contextExpiryTimeSpan;
        private readonly TimeSpan _preProcessExpiryTimeSpan;

        public byte[]? this[byte[] key]
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
                while (true)
                {
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

                    byte[]? fromMem = _tempDb[key] ?? _stateDb[key];
                    if (fromMem == null)
                    {
                        if (_logger.IsTrace) _logger.Trace($"Beam sync miss - {key.ToHexString()} - retrieving");

                        if (BeamSyncContext.Cancelled.Value.IsCancellationRequested)
                        {
                            throw new BeamCanceledException("Beam cancellation requested");
                        }

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
                            string message = $"Beam sync request {BeamSyncContext.Description.Value} for key {key.ToHexString()} with last update on {BeamSyncContext.LastFetchUtc.Value:hh:mm:ss.fff} has expired";
                            if (_logger.IsDebug) _logger.Debug(message);
                            throw new BeamSyncException(message);
                        }

                        wasInDb = false;
                        // _logger.Info($"BEAM SYNC Asking for {key.ToHexString()} - resolved keys so far {_resolvedKeysCount}");

                        lock (_requestedNodes)
                        {
                            _requestedNodes.Add(new Keccak(key));
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
                            Interlocked.Increment(ref Metrics.BeamedTrieNodes);
                            if (_logger.IsDebug)
                                _logger.Debug(
                                    $"Resolved key {key.ToHexString()} of context {BeamSyncContext.Description.Value} - resolved ctx {BeamSyncContext.ResolvedInContext.Value} | total {Metrics.BeamedTrieNodes}");
                        }
                        else
                        {
                            if (VerifiedModeEnabled
                                && !Bytes.AreEqual(Keccak.Compute(fromMem).Bytes, key))
                            {
                                if (_logger.IsWarn) _logger.Warn($"DB had an entry with a hash mismatch {key.ToHexString()} vs {Keccak.Compute(fromMem).Bytes.ToHexString()}");
                                _tempDb[key] = null;
                                _stateDb[key] = null;
                                continue;
                            }
                        }

                        BeamSyncContext.LastFetchUtc.Value = DateTime.UtcNow;
                        return fromMem;
                    }
                }
            }

            set
            {
                if (_logger.IsTrace) _logger.Trace($"Saving to temp - {key.ToHexString()}");
                _targetDbForSaves[key] = value;
            }
        }

        public KeyValuePair<byte[], byte[]?>[] this[byte[][] keys] =>
            keys.Select(k => new KeyValuePair<byte[], byte[]?>(k, this[k])).ToArray();

        public IEnumerable<KeyValuePair<byte[], byte[]>> GetAll(bool ordered = false)
        {
            throw new NotSupportedException();
        }

        public IEnumerable<byte[]> GetAllValues(bool ordered = false)
        {
            throw new NotSupportedException();
        }

        public IBatch StartBatch()
        {
            return this.LikeABatch();
        }

        public void Remove(byte[] key)
        {
            _targetDbForSaves.Remove(key);
        }

        public bool KeyExists(byte[] key)
        {
            throw new NotSupportedException("Key exists is not supported by beam sync.");
        }

        public IDb Innermost => _stateDb.Innermost;

        public void Flush()
        {
            // this should never get flushed except for some dispose scenarios?
        }

        public void Clear()
        {
            _tempDb.Clear();
            _stateDb.Clear();
        }

        public override Task<StateSyncBatch?> PrepareRequest()
        {
            StateSyncBatch? request;
            lock (_requestedNodes)
            {
                if (_requestedNodes.Count == 0)
                {
                    return Task.FromResult((StateSyncBatch?) null);
                }

                StateSyncItem[] requestedNodes;
                if (_requestedNodes.Count < StateSyncFeed.MaxRequestSize)
                {
                    // do not make it state sync item :)
                    requestedNodes = _requestedNodes.Select(n => new StateSyncItem(n, NodeDataType.State)).ToArray();
                    _requestedNodes.Clear();
                }
                else
                {
                    Keccak[] source = _requestedNodes.ToArray();
                    requestedNodes = new StateSyncItem[StateSyncFeed.MaxRequestSize];
                    _requestedNodes.Clear();
                    for (int i = 0; i < source.Length; i++)
                    {
                        if (i < StateSyncFeed.MaxRequestSize)
                        {
                            // not state sync item
                            requestedNodes[i] = new StateSyncItem(source[i], NodeDataType.State);
                        }
                        else
                        {
                            _requestedNodes.Add(source[i]);
                        }
                    }
                }

                request = new StateSyncBatch(requestedNodes);
                request.ConsumerId = FeedId;
            }

            Interlocked.Increment(ref Metrics.BeamedRequests);
            return Task.FromResult<StateSyncBatch?>(request);
        }

        public override SyncResponseHandlingResult HandleResponse(StateSyncBatch? batch)
        {
            if (batch == null)
            {
                if (_logger.IsWarn) _logger.Warn($"{nameof(BeamSyncDb)} received a NULL batch as a response.");
                return SyncResponseHandlingResult.InternalError;
            }

            if (batch.ConsumerId != FeedId)
            {
                if (_logger.IsWarn) _logger.Warn($"Beam sync response sent by feed {batch.ConsumerId} came back to feed {FeedId}");
                return SyncResponseHandlingResult.InternalError;
            }

            bool wasDataInvalid = false;
            int consumed = 0;

            byte[][]? data = batch.Responses;
            if (data != null)
            {
                for (int i = 0; i < Math.Min(data.Length, batch.RequestedNodes.Length); i++)
                {
                    Keccak key = batch.RequestedNodes[i].Hash;
                    if (data[i] != null)
                    {
                        if (Keccak.Compute(data[i]) == key)
                        {
                            _targetDbForSaves[key.Bytes] = data[i];
                            // _requestedNodes.Remove(hashes[i], out _);
                            consumed++;
                        }
                        else
                        {
                            wasDataInvalid = true;
                            if (_logger.IsDebug) _logger.Debug("Received node data which does not match the hash.");
                        }
                    }
                    else
                    {
                        if (_targetDbForSaves[key.Bytes] == null)
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
            if (wasDataInvalid)
            {
                return SyncResponseHandlingResult.LesserQuality;
            }

            return consumed == 0 ? SyncResponseHandlingResult.NoProgress : SyncResponseHandlingResult.OK;
        }

        /// <summary>
        /// not sure if this synchronization is still needed nowadays?
        /// </summary>
        private AutoResetEvent _autoReset = new(true);

        public override bool IsMultiFeed => false;
        public override AllocationContexts Contexts => AllocationContexts.State;
        public bool VerifiedModeEnabled { get; set; }
    }
}
