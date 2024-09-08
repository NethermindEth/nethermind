// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

public interface IWorldStateProvider
{
    public IWorldState GetGlobalWorldState(BlockHeader header);

    public IStateReader GetGlobalStateReader();

    public IWorldState GetWorldState();

    public ITrieStore TrieStore { get; }
}
