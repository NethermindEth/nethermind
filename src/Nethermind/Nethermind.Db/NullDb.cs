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
using Nethermind.Core;

namespace Nethermind.Db
{
    public class NullDb : IDb
    {
        private NullDb()
        {
        }

        private static NullDb? _instance;
        
        public static NullDb Instance => LazyInitializer.EnsureInitialized(ref _instance, () => new NullDb());

        public string Name { get; } = "NullDb";

        public byte[]? this[byte[] key]
        {
            get => null;
            set => throw new NotSupportedException();
        }

        public KeyValuePair<byte[], byte[]>[] this[byte[][] keys] => keys.Select(k => new KeyValuePair<byte[], byte[]>(k, null)).ToArray(); 

        public void Remove(byte[] key)
        {
            throw new NotSupportedException();
        }

        public bool KeyExists(byte[] key)
        {
            return false;
        }

        public IDb Innermost => this;
        public void Flush() { }
        public void Clear() { }

        public IEnumerable<KeyValuePair<byte[], byte[]>> GetAll(bool ordered = false) => Enumerable.Empty<KeyValuePair<byte[], byte[]>>();

        public IEnumerable<byte[]> GetAllValues(bool ordered = false) => Enumerable.Empty<byte[]>();

        public IBatch StartBatch()
        {
            throw new NotSupportedException();
        }

        public void Dispose()
        {
        }
    }
}
