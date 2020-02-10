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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;

namespace Nethermind.Store.BeamSync
{
    public class BeamSyncDb : IDb, INodeDataConsumer
    {
        private readonly string _description;
        public UInt256 RequiredPeerDifficulty { get; private set; } = UInt256.Zero;

        /// <summary>
        /// This DB should be not in memory, in fact we should write to the underlying rocksDb and only keep a cache in memory
        /// </summary>
        private IDb _db;

        private ILogger _logger;

        public BeamSyncDb(IDb db, string description, ILogManager logManager)
        {
            _description = description;
            _logger = logManager.GetClassLogger<BeamSyncDb>();
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        public void Dispose()
        {
            _db.Dispose();
        }

        public string Name => _db.Name;

        private int _resolvedKeysCount;

        private object _diffLock = new object();

        private HashSet<Keccak> _requestedNodes = new HashSet<Keccak>();

        private HashSet<Keccak> _sentNotSaved = new HashSet<Keccak>();

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
                while (true)
                {
                    var fromMem = _db[key];
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
                            throw new StateException(message);
                        }

                        wasInDb = false;
                        bool wasSent = false;
                        lock (_sentNotSaved)
                        {
                            wasSent = _sentNotSaved.Contains(new Keccak(key));
                        }
                        
                        if (!wasSent)
                        {
                            // _logger.Info($"BEAM SYNC Asking for {key.ToHexString()} - resolved keys so far {_resolvedKeysCount}");

                            lock (_requestedNodes)
                            {
                                _requestedNodes.Add(new Keccak(key));
                            }

                            // _logger.Error($"Requested {key.ToHexString()}");

                            NeedMoreData?.Invoke(this, EventArgs.Empty);
                        }

                        _autoReset.WaitOne(50);
                    }
                    else
                    {
                        if (!wasInDb)
                        {
                            if (_logger.IsInfo) _logger.Info($"{_description} Resolved key {key.ToHexString()} of context {BeamSyncContext.Description.Value} - resolved: {_resolvedKeysCount} | pending: {_requestedNodes.Count}");
                            Interlocked.Increment(ref _resolvedKeysCount);
                        }

                        BeamSyncContext.LastFetchUtc.Value = DateTime.UtcNow;
                        return fromMem;
                    }
                }
            }

            set => _db[key] = value;
        }

        public byte[][] GetAll()
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
            _db.Remove(key);
        }

        public bool KeyExists(byte[] key)
        {
            return _db.KeyExists(key);
        }

        public IDb Innermost => _db.Innermost;

        public event EventHandler NeedMoreData;

        public Keccak[] PrepareRequest()
        {
            Keccak[] request;
            lock (_requestedNodes)
            {
                if (_requestedNodes.Count == 0)
                {
                    return Array.Empty<Keccak>();
                }
                
                if (_requestedNodes.Count < 256)
                {
                    request = _requestedNodes.ToArray();
                    _requestedNodes.Clear();
                }
                else
                {
                    Keccak[] source = _requestedNodes.ToArray();
                    request = new Keccak[256];
                    _requestedNodes.Clear();
                    for (int i = 0; i < source.Length; i++)
                    {
                        if (i < 256)
                        {
                            request[i] = source[i];
                        }
                        else
                        {
                            _requestedNodes.Add(source[i]);
                        }
                    }
                }
            }

            // if (_logger.IsInfo) _logger.Info($"Sending request for {request.Length} | resolved: {_resolvedKeysCount} | pending: {_requestedNodes.Count} | not_yet {_sentNotSaved.Count()}");
            lock (_sentNotSaved)
            {
                for (int i = 0; i < request.Length; i++)
                {
                    _sentNotSaved.Add(request[i]);
                }
            }

            return request;
        }

        public int HandleResponse(Keccak[] hashes, byte[][] data)
        {
            int consumed = 0;
            if (data != null)
            {
                for (int i = 0; i < hashes.Length; i++)
                {
                    if (data.Length > i && data[i] != null)
                    {
                        if (Keccak.Compute(data[i]) == hashes[i])
                        {
                            _db[hashes[i].Bytes] = data[i];
                            lock (_sentNotSaved)
                            {
                                _sentNotSaved.Remove(hashes[i]);
                            }

                            // _requestedNodes.Remove(hashes[i], out _);
                            consumed++;
                        }
                    }
                    else
                    {
                        if (_db[hashes[i].Bytes] == null)
                        {
                            lock (_requestedNodes)
                            {
                                _requestedNodes.Add(hashes[i]);
                            }
                        }
                    }
                }
            }

            _autoReset.Set();
            return consumed;
        }

        private AutoResetEvent _autoReset = new AutoResetEvent(true);
    }
}