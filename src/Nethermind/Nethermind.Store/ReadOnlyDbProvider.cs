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

using System;

namespace Nethermind.Store
{
    public class ReadOnlyDbProvider : IReadOnlyDbProvider
    {
        public ReadOnlyDbProvider(IDbProvider wrappedProvider, bool createInMemoryWriteStore)
        {
            wrappedProvider = wrappedProvider ?? throw new ArgumentNullException(nameof(wrappedProvider));
            StateDb = new StateDb(new ReadOnlyDb(wrappedProvider.StateDb, createInMemoryWriteStore));
            CodeDb = new StateDb(new ReadOnlyDb(wrappedProvider.CodeDb, createInMemoryWriteStore));
            ReceiptsDb = new ReadOnlyDb(wrappedProvider.ReceiptsDb, createInMemoryWriteStore);
            BlockInfosDb = new ReadOnlyDb(wrappedProvider.BlockInfosDb, createInMemoryWriteStore);
            BlocksDb = new ReadOnlyDb(wrappedProvider.BlocksDb, createInMemoryWriteStore);
        }

        public void Dispose()
        {
        }

        public ISnapshotableDb StateDb { get; }
        public ISnapshotableDb CodeDb { get; }
        public IDb ReceiptsDb { get; }
        public IDb BlocksDb { get; }
        public IDb BlockInfosDb { get; }
    }
}