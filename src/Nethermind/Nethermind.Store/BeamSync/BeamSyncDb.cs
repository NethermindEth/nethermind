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
using System.Threading;
using Nethermind.Core.Crypto;

namespace Nethermind.Store.BeamSync
{
    public class BeamSyncDb : IDb, INodeDataConsumer
    {
        private MemDb _memDb = new MemDb();

        public void Dispose()
        {
        }

        public string Name => "BeamSyncDb";

        private ConcurrentQueue<Keccak> _requestedNodes =new ConcurrentQueue<Keccak>(); 
        
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
                        _requestedNodes.Enqueue(new Keccak(key));
                        NeedsData = true;
                        _autoReset.WaitOne();
                    }
                    else
                    {
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
            List<Keccak> request = new List<Keccak>();
            while (_requestedNodes.TryDequeue(out Keccak requestedNode))
            {
                request.Add(requestedNode);
            }

            return request.ToArray();
        }

        public void HandleResponse(Keccak[] hashes, byte[][] data)
        {
            _autoReset.Set();
        }

        private AutoResetEvent _autoReset = new AutoResetEvent(true);
        
        public bool NeedsData { get; private set; }
    }
}