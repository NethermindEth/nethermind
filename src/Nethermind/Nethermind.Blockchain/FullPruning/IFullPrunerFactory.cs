// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State;
using Nethermind.Trie.Pruning;

namespace Nethermind.Blockchain.FullPruning;

public interface IFullPrunerFactory
{
    FullPruner? Create(IStateReader stateReader, IPruningTrieStore trieStore);
}
