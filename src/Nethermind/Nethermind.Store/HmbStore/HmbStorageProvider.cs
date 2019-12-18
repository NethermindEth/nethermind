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
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.Store.HmbStore
{
    public class HmbStorageProvider : IStorageProvider, INodeDataConsumer
    {
        private ILogger _logger;
        
        public HmbStorageProvider(ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger<HmbStorageProvider>() ?? throw new ArgumentNullException(nameof(logManager));
        }
        
        public byte[] GetOriginal(StorageAddress storageAddress)
        {
            throw new NotImplementedException();
        }

        public byte[] Get(StorageAddress storageAddress)
        {
            throw new NotImplementedException();
        }

        public void Set(StorageAddress storageAddress, byte[] newValue)
        {
            throw new NotImplementedException();
        }

        public void Reset()
        {
            throw new NotImplementedException();
        }

        public void Destroy(Address address)
        {
            throw new NotImplementedException();
        }

        public void CommitTrees()
        {
            throw new NotImplementedException();
        }

        public void Restore(int snapshot)
        {
            throw new NotImplementedException();
        }

        public void Commit()
        {
            throw new NotImplementedException();
        }

        public void Commit(IStorageTracer stateTracer)
        {
            throw new NotImplementedException();
        }

        public int TakeSnapshot()
        {
            throw new NotImplementedException();
        }

        public event EventHandler NeedMoreData;
        public Keccak[] PrepareRequest()
        {
            throw new NotImplementedException();
        }
    }
}