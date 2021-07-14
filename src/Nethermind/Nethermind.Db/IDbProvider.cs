// Copyright (c) 2020 Demerzel Solutions Limited
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
    public enum DbModeHint
    {
        Mem,
        Persisted
    }

    public interface IDbProvider : IDisposable
    {
        DbModeHint DbMode { get; }
        public IDb StateDb => GetDb<IDb>(DbNames.State);
        public IDb CodeDb => GetDb<IDb>(DbNames.Code);
        public IColumnsDb<ReceiptsColumns> ReceiptsDb => GetDb<IColumnsDb<ReceiptsColumns>>(DbNames.Receipts);
        public IDb BlocksDb => GetDb<IDb>(DbNames.Blocks);
        public IDb HeadersDb => GetDb<IDb>(DbNames.Headers);
        public IDb BlockInfosDb => GetDb<IDb>(DbNames.BlockInfos);

        // BloomDB progress / config (does not contain blooms - they are kept in bloom storage)
        public IDb BloomDb => GetDb<IDb>(DbNames.Bloom);

        // LES (ignore)
        public IDb ChtDb => GetDb<IDb>(DbNames.CHT);
        
        // Beam Sync (StateDB like)
        IDb? BeamTempDb { get; }
        IDb WitnessDb => GetDb<IDb>(DbNames.Witness);

        T GetDb<T>(string dbName) where T : class, IDb;

        void RegisterDb<T>(string dbName, T db) where T : class, IDb;

        IDictionary<string, IDb> RegisteredDbs { get; }
    }
}
