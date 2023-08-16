// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning;

public interface ISyncTrieStore
{
    bool IsFullySynced(Keccak stateRoot);
}
