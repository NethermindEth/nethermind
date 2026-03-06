// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Synchronization.SnapSync;

namespace Nethermind.Synchronization.Test.SnapSync;

internal class TestSnapTrieFactory(Func<ISnapTree> createTree) : ISnapTrieFactory
{
    public ISnapTree CreateStateTree() => createTree();
    public ISnapTree CreateStorageTree(in ValueHash256 accountPath) => createTree();
}
