// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.State.Snap;
using Nethermind.Synchronization.SnapSync;

namespace Nethermind.Synchronization.Test.SnapSync;

internal class TestSnapTrieFactory(
    Func<ISnapTree<PathWithAccount>> createStateTree,
    Func<ISnapTree<PathWithStorageSlot>>? createStorageTree = null) : ISnapTrieFactory
{
    public ISnapTree<PathWithAccount> CreateStateTree() => createStateTree();
    public ISnapTree<PathWithStorageSlot> CreateStorageTree(in ValueHash256 accountPath) =>
        createStorageTree is not null ? createStorageTree() : throw new NotSupportedException("No storage tree factory provided");
}
