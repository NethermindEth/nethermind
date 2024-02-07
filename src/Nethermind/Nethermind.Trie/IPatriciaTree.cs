// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie.Pruning;

namespace Nethermind.Trie;

public interface IPatriciaTree
{
    Hash256 RootHash { get; set; }
    Hash256 ParentStateRootHash { get; set; }
    TrieNode? RootRef { get; set; }
    byte[] StoreNibblePathPrefix { get; }
    void UpdateRootHash();
    void Commit(long blockNumber, bool skipRoot = false, WriteFlags writeFlags = WriteFlags.None);

    ITrieStore TrieStore { get; }
    void Accept(ITreeVisitor visitor, Hash256 rootHash, VisitingOptions? visitingOptions = null);
}
