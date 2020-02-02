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
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.Store.BeamSync
{
    public class BeamSyncDb : IDb, INodeDataConsumer
    {
        private MemDb _memDb = new MemDb();

        private ILogger _logger;
        
        public BeamSyncDb(ILogManager logManager)
        {
            _logger = logManager.GetClassLogger<BeamSyncDb>();
        }
        
        public void Dispose()
        {
        }

        public string Name => "Hmb";

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
                
                while (true)
                {
                    var fromMem = _memDb[key];
                    if (fromMem == null)
                    {
                        // we store sync progress data at Keccak.Zero;
                        if (Bytes.AreEqual(key, Keccak.Zero.Bytes))
                        {
                            return null;
                        }

                        _requestedNodes.Add(new Keccak(key));
                        // _logger.Error($"Requested {key.ToHexString()}");

                        NeedsData = true;
                        NeedMoreData?.Invoke(this, EventArgs.Empty);
                        _autoReset.WaitOne();
                    }
                    else
                    {
                        _requestedNodes.Clear();
                        _logger.Info($"BEAM SYNC Resolved {key.ToHexString()} - db size {_memDb.Keys.Count}");
                        return fromMem;
                    }
                }
            }

            set => _memDb[key] = value;
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
            _memDb.Remove(key);
        }

        public bool KeyExists(byte[] key)
        {
            return _memDb.KeyExists(key);
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
                    _memDb[hashes[i].Bytes] = data[i];
                    consumed++;
                }
                
            }

            _autoReset.Set();
            return consumed;
        }

        private AutoResetEvent _autoReset = new AutoResetEvent(false);

        public bool NeedsData { get; private set; }
    }
}