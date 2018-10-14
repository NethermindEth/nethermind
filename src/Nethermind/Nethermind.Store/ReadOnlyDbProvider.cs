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
    public class ReadOnlyDbProvider : IDbProvider
    {
        private readonly IDbProvider _wrappedProvider;

        public ReadOnlyDbProvider(IDbProvider wrappedProvider)
        {
            _wrappedProvider = wrappedProvider ?? throw new ArgumentNullException(nameof(wrappedProvider));
            StateDb = new StateDb(new ReadOnlyDb(wrappedProvider.StateDb));
            CodeDb = new StateDb(new ReadOnlyDb(wrappedProvider.CodeDb));
            ReceiptsDb = new ReadOnlyDb(wrappedProvider.ReceiptsDb);
            BlockInfosDb = new ReadOnlyDb(wrappedProvider.BlockInfosDb);
            BlocksDb = new ReadOnlyDb(wrappedProvider.BlocksDb);
        }
        
        public void Dispose()
        {
            StateDb.Dispose();
            CodeDb.Dispose();
            ReceiptsDb.Dispose();
            BlockInfosDb.Dispose();
            BlocksDb.Dispose();
        }

        public ISnapshotableDb StateDb { get; }
        public ISnapshotableDb CodeDb { get; }
        public IDb ReceiptsDb { get; }
        public IDb BlocksDb { get; }
        public IDb BlockInfosDb { get; }
    }
}