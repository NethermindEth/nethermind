// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

public interface IStorageTreeFactory
{
    StorageTree Create(Address address, ITrieStore trieStore, Keccak storageRoot, Keccak stateRoot, ILogManager? logManager);
}
