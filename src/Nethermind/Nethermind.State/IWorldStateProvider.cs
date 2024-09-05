// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

public interface IWorldStateProvider
{
    public IWorldState GetWorldState(BlockHeader header);

    public IStateReader GetStateReader(Hash256 stateRoot);

    ITrieStore TrieStore { get; }
}
