// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Autofac.Features.AttributeFilters;
using Nethermind.Core.Crypto;
using Nethermind.Db;

namespace Nethermind.Synchronization.Test.SnapSync;

public interface ISnapTestHelper
{
    int CountTrieNodes();
    bool TrieNodeKeyExists(Hash256 hash);
    long TrieNodeWritesCount { get; }
}

public class PatriciaSnapTestHelper([KeyFilter(DbNames.State)] IDb stateDb) : ISnapTestHelper
{
    public int CountTrieNodes() => stateDb.GetAllKeys().Count();
    public bool TrieNodeKeyExists(Hash256 hash) => stateDb.KeyExists(hash.Bytes);
    public long TrieNodeWritesCount => ((MemDb)stateDb).WritesCount;
}
