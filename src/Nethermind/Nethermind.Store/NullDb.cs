/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System.Collections.Generic;
using System.Threading;

namespace Nethermind.Store
{
    public class NullDb : IDb
    {
        private NullDb()
        {
        }

        private static NullDb _instance;
        public static NullDb Instance => LazyInitializer.EnsureInitialized(ref _instance, () => new NullDb());

        public string Name { get; } = "NullDb";

        public byte[] this[byte[] key]
        {
            get => null;
            set => throw new System.NotSupportedException();
        }

        public void Remove(byte[] key)
        {
            throw new System.NotSupportedException();
        }

        public bool KeyExists(byte[] key)
        {
            return false;
        }

        public byte[][] GetAll()
        {
            throw new System.NotImplementedException();
        }

        public void StartBatch()
        {
            throw new System.NotSupportedException();
        }

        public void CommitBatch()
        {
            throw new System.NotSupportedException();
        }

        public ICollection<byte[]> Keys { get; }
        public ICollection<byte[]> Values { get; }

        public void Dispose()
        {
        }
    }
}