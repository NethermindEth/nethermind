// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Db;

namespace Nethermind.Blockchain.Test.PartialArchive;

internal sealed class SingleStateDbProvider(IDb stateDb) : IDbProvider
{
    public T GetDb<T>(string dbName) where T : class, IDb => (T)stateDb;
    public IColumnsDb<T> GetColumnDb<T>(string dbName) => throw new NotSupportedException();
    public void Dispose() { }
}
