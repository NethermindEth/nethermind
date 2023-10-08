// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
        public IDb BlockNumbersDb => GetDb<IDb>(DbNames.BlockNumbers);
        public IDb BlockInfosDb => GetDb<IDb>(DbNames.BlockInfos);

        // BloomDB progress / config (does not contain blooms - they are kept in bloom storage)
        public IDb BloomDb => GetDb<IDb>(DbNames.Bloom);

        // LES (ignore)
        public IDb ChtDb => GetDb<IDb>(DbNames.CHT);

        public IDb WitnessDb => GetDb<IDb>(DbNames.Witness);

        public IDb MetadataDb => GetDb<IDb>(DbNames.Metadata);

        T GetDb<T>(string dbName) where T : class, IDb;

        void RegisterDb<T>(string dbName, T db) where T : class, IDb;

        IDictionary<string, IDb> RegisteredDbs { get; }
    }
}
