// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Trie;

namespace Nethermind.Synchronization.SnapSync;

public interface ISnapStorageTree : ISnapTree
{
    Hash256 RootHash { get; }

    void BulkSet(in ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries, PatriciaTree.Flags flags);
    void UpdateRootHash();
    void Commit(WriteFlags writeFlags);
}
