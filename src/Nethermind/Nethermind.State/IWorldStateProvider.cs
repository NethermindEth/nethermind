// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

public interface IWorldStateProvider
{
    public IWorldState GetGlobalWorldState(BlockHeader header);

    public IStateReader GetGlobalStateReader();

    public IWorldState GetWorldState();

    public ITrieStore TrieStore { get; }

    bool HasStateForRoot(Hash256 stateRoot);

    void RunTreeVisitor(ITreeVisitor treeVisitor, Hash256 stateRoot, VisitingOptions? visitingOptions = null);

    void SetStateRoot(Hash256 stateRoot);

    void Reset();
}
