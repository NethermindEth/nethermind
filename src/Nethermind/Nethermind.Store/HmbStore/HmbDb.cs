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
using System.Threading;
using Nethermind.Core.Crypto;

namespace Nethermind.Store.HmbStore
{
    public class HmbDb : IDb, INodeDataConsumer
    {
        private MemDb _memDb = new MemDb();

        public void Dispose()
        {
        }

        public string Name => "Hmb";

        public byte[] this[byte[] key]
        {
            get
            {
                var fromMem = _memDb[key];
                if (fromMem == null)
                {
                    NeedMoreData?.Invoke(this, EventArgs.Empty);
                    Thread.Sleep(100);
                }
                
                return fromMem;
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
            throw new NotImplementedException();
        }
    }
}