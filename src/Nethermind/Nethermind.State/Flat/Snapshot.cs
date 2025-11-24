// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

/// <summary>
/// Snapshot are written keys between state From to state To
/// </summary>
/// <param name="From"></param>
/// <param name="To"></param>
/// <param name="Accounts"></param>
/// <param name="Storages"></param>
public readonly record struct Snapshot(
    StateId From,
    StateId To,
    Dictionary<Address, Account?> Accounts,
    Dictionary<(Address, UInt256), byte[]> Storages,
    HashSet<ValueHash256> SelfDestructedStorages,
    HashSet<Address> SelfDestructedStorageAddresses,
    Dictionary<(Hash256, TreePath), TrieNode> TrieNodes)
{
}
