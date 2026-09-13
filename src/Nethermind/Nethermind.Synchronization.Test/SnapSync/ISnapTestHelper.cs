// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.State.Flat;

namespace Nethermind.Synchronization.Test.SnapSync;

public interface ISnapTestHelper
{
    int CountTrieNodes();
    bool TrieNodeKeyExists(Hash256 hash);
    long TrieNodeWritesCount { get; }
}

public class FlatSnapTestHelper(IColumnsDb<FlatDbColumns> columnsDb) : ISnapTestHelper
{
    private IDb StateTopNodes => columnsDb.GetColumnDb(FlatDbColumns.StateTopNodes);
    private IDb StateNodes => columnsDb.GetColumnDb(FlatDbColumns.StateNodes);
    private IDb StorageNodes => columnsDb.GetColumnDb(FlatDbColumns.StorageNodes);
    private IDb FallbackNodes => columnsDb.GetColumnDb(FlatDbColumns.FallbackNodes);

    public int CountTrieNodes() => StateTopNodes.GetAllKeys().Count() + StateNodes.GetAllKeys().Count()
        + StorageNodes.GetAllKeys().Count() + FallbackNodes.GetAllKeys().Count();

    public bool TrieNodeKeyExists(Hash256 hash) =>
        StateTopNodes.GetAllValues().Concat(StateNodes.GetAllValues()).Concat(StorageNodes.GetAllValues()).Concat(FallbackNodes.GetAllValues())
            .Any(value => ValueKeccak.Compute(value) == hash);

    public long TrieNodeWritesCount => ((SnapshotableMemDb)StateTopNodes).WritesCount
        + ((SnapshotableMemDb)StateNodes).WritesCount
        + ((SnapshotableMemDb)StorageNodes).WritesCount
        + ((SnapshotableMemDb)FallbackNodes).WritesCount;
}
