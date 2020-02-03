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
using System.IO;
using System.Linq;
using System.Threading;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.Store.BeamSync
{
    public class BeamSyncDb : IDb, INodeDataConsumer
    {
        private readonly string _description;
        private static DateTime _lastProgressInContext;
        
        /// <summary>
        /// This can be in this hacky way as it will obviously have to be changed
        /// There must be some BeamSync managing class passed to various processors so this info can be shared
        /// Also some better design is needed for how to decide that beam sync has to be dropped for given context
        /// </summary>
        public static object Context
        {
            get => _context;
            set
            {
                _context = value;
                // Console.WriteLine("Starting new context: " + _context);
                _lastProgressInContext = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// This DB should be not in memory, in fact we should write to the underlying rocksDb and only keep a cache in memory
        /// </summary>
        private IDb _db;

        private ILogger _logger;
        
        public BeamSyncDb(IDb db, string description, ILogManager logManager)
        {
            _description = description;
            _logger = logManager.GetClassLogger<BeamSyncDb>();
            // _db = db ?? throw new ArgumentNullException(nameof(db));
            _db = new MemDb();
        }
        
        public void Dispose()
        {
        }

        public string Name => _db.Name;

        private int _resolvedKeysCount;
        
        private HashSet<Keccak> _requestedNodes = new HashSet<Keccak>();

        public byte[] this[byte[] key]
        {
            get
            {
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
                        wasInDb = false;
                        _logger.Info($"BEAM SYNC Asking for {key.ToHexString()} - resolved keys so far {_resolvedKeysCount}");
                        // we store sync progress data at Keccak.Zero;
                        if (Bytes.AreEqual(key, Keccak.Zero.Bytes))
                        {
                            return null;
                        }

                        _requestedNodes.Add(new Keccak(key));
                        // _logger.Error($"Requested {key.ToHexString()}");

                        NeedsData = true;
                        NeedMoreData?.Invoke(this, EventArgs.Empty);
                        _autoReset.WaitOne(1000);
                        if (DateTime.UtcNow - _lastProgressInContext > TimeSpan.FromSeconds(15))
                        {
                            // _logger.Error($"Context failure for {_context}");
                            // throw new InvalidDataException("Context fail in beam sync");
                        }
                    }
                    else
                    {
                        _lastProgressInContext = DateTime.UtcNow;
                        _requestedNodes.Clear();

                        if (!wasInDb)
                        {
                            if(_logger.IsInfo) _logger.Info($"{_description} BEAM SYNC Resolved {key.ToHexString()} - resolved keys so far {_resolvedKeysCount}");
                            _resolvedKeysCount++;
                        }

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

        public event EventHandler NeedMoreData;

        public Keccak[] PrepareRequest()
        {
            NeedsData = false;
            return _requestedNodes.ToArray();
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
                        _db[hashes[i].Bytes] = data[i];
                        consumed++;
                    }
                }
            }

            _autoReset.Set();
            return consumed;
        }

        private AutoResetEvent _autoReset = new AutoResetEvent(false);
        private static object _context;

        public bool NeedsData { get; private set; }
    }
}