// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Core.Verkle;
using Nethermind.Verkle.Tree.Sync;
using Nethermind.Verkle.Tree.VerkleDb;

namespace Nethermind.Verkle.Tree.TreeStore;

public interface IVerkleSyncTreeStore
{
    public IEnumerable<KeyValuePair<byte[], byte[]>> GetLeafRangeIterator(byte[] fromRange, byte[] toRange,
        Hash256 stateRoot);

    public IEnumerable<PathWithSubTree> GetLeafRangeIterator(Stem fromRange, Stem toRange, Hash256 stateRoot,
        long bytes);

    public void InsertRootNodeAfterSyncCompletion(byte[] rootHash, long blockNumber);
    public void InsertSyncBatch(long blockNumber, VerkleMemoryDb batch);
}
