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

namespace Nethermind.Db
{
    public class ReadOnlyDbProvider : IReadOnlyDbProvider
    {
        private readonly IDbProvider _wrappedProvider;
        private readonly bool _createInMemoryWriteStore;
        private List<ReadOnlyDb> _otherDbs = new List<ReadOnlyDb>();
        public ReadOnlyDb NestedStateDb { get; }
        public ReadOnlyDb NestedCodeDb { get; }
        
        public ReadOnlyDbProvider(IDbProvider wrappedProvider, bool createInMemoryWriteStore)
        {
            _wrappedProvider = wrappedProvider;
            _createInMemoryWriteStore = createInMemoryWriteStore;
            if (wrappedProvider == null)
            {
                throw new ArgumentNullException(nameof(wrappedProvider));
            }

            NestedStateDb = new ReadOnlyDb(wrappedProvider.StateDb, createInMemoryWriteStore);
            StateDb = new StateDb(NestedStateDb);
            NestedCodeDb = new ReadOnlyDb(wrappedProvider.CodeDb, createInMemoryWriteStore);
            CodeDb = new StateDb(NestedCodeDb);
            // StateDb = new ReadOnlyDb(wrappedProvider.StateDb, createInMemoryWriteStore);
            // CodeDb = new ReadOnlyDb(wrappedProvider.CodeDb, createInMemoryWriteStore);
            NestedReceiptsDb = new ReadOnlyColumnsDb<ReceiptsColumns>(wrappedProvider.ReceiptsDb, createInMemoryWriteStore);
            NestedBlockInfosDb = new ReadOnlyDb(wrappedProvider.BlockInfosDb, createInMemoryWriteStore);
            NestedBlocksDb = new ReadOnlyDb(wrappedProvider.BlocksDb, createInMemoryWriteStore);
            NestedHeadersDb = new ReadOnlyDb(wrappedProvider.HeadersDb, createInMemoryWriteStore);
            NestedPendingTxsDb = new ReadOnlyDb(wrappedProvider.PendingTxsDb, createInMemoryWriteStore);
            NestedConfigsDb = new ReadOnlyDb(wrappedProvider.ConfigsDb, createInMemoryWriteStore);
            NestedEthRequestsDb = new ReadOnlyDb(wrappedProvider.EthRequestsDb, createInMemoryWriteStore);
            NestedBloomDb = new ReadOnlyDb(wrappedProvider.BloomDb, createInMemoryWriteStore);
            NestedChtDb = new ReadOnlyDb(wrappedProvider.ChtDb, createInMemoryWriteStore);

            foreach (var otherDb in wrappedProvider.OtherDbs)
            {
                _otherDbs.Add(new ReadOnlyDb(otherDb, createInMemoryWriteStore));
            }
        }

        public void Dispose()
        {
            // ToDo why wee don't dispose dbs here - investigate it or consult with someone
        }

        public ISnapshotableDb StateDb { get; }
        public ISnapshotableDb CodeDb { get; }
        public IColumnsDb<ReceiptsColumns> ReceiptsDb => NestedReceiptsDb;
        public IDb BlocksDb => NestedBlocksDb;
        public IDb HeadersDb => NestedHeadersDb;
        public IDb BlockInfosDb => NestedBlockInfosDb;
        public IDb PendingTxsDb => NestedPendingTxsDb;
        public IDb ConfigsDb => NestedConfigsDb;
        public IDb EthRequestsDb => NestedEthRequestsDb;
        public IDb BloomDb => NestedBloomDb;
        public IDb ChtDb => NestedChtDb;
        public IDb BeamStateDb { get; } = new MemDb(); 
        public ReadOnlyColumnsDb<ReceiptsColumns> NestedReceiptsDb { get; }
        public ReadOnlyDb NestedBlocksDb { get; }
        public ReadOnlyDb NestedHeadersDb { get; }
        public ReadOnlyDb NestedBlockInfosDb { get; }
        public ReadOnlyDb NestedPendingTxsDb { get; }
        public ReadOnlyDb NestedConfigsDb { get; }
        public ReadOnlyDb NestedEthRequestsDb { get; }
        public ReadOnlyDb NestedBloomDb { get; }
        public ReadOnlyDb NestedChtDb { get; }

        public IEnumerable<IDb> OtherDbs => _otherDbs;

        public void ClearTempChanges()
        {
            StateDb.Restore(-1);
            CodeDb.Restore(-1);
            NestedReceiptsDb.Restore(-1);
            NestedBlocksDb.Restore(-1);
            NestedHeadersDb.Restore(-1);
            NestedBlockInfosDb.Restore(-1);
            NestedConfigsDb.Restore(-1);
            NestedEthRequestsDb.Restore(-1); 
            NestedReceiptsDb.Restore(-1);
            NestedBloomDb.Restore(-1);
            NestedChtDb.Restore(-1);
            
            foreach(var otherDb in _otherDbs)
            {
                otherDb.Restore(-1);
            }

            BeamStateDb.Clear();
        }

        public IDb RegisterDb(string name, IPlugableDbConfig config)
        {
            var newDb = _wrappedProvider.RegisterDb(name, config);
            var newReadonlyDb = new ReadOnlyDb(newDb, _createInMemoryWriteStore);
            _otherDbs.Add(newReadonlyDb);
            return newReadonlyDb;
        }
    }
}
