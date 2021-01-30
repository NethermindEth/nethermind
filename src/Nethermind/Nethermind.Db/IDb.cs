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
using Nethermind.Core;

namespace Nethermind.Db
{
    public interface IDb : IKeyValueStoreWithBatching, IDisposable
    {
        string Name { get; }
        KeyValuePair<byte[],byte[]?>[] this[byte[][] keys] { get; }
        IEnumerable<KeyValuePair<byte[], byte[]>> GetAll(bool ordered = false);
        IEnumerable<byte[]> GetAllValues(bool ordered = false);
        void Remove(byte[] key);
        bool KeyExists(byte[] key);

        /// <summary>
        /// For nested DB structures returns the innermost one
        /// </summary>
        public IDb Innermost { get; }

        void Flush();
        
        void Clear();

        public IReadOnlyDb CreateReadOnly(bool createInMemWriteStore) => new ReadOnlyDb(this, createInMemWriteStore);
    }
}
