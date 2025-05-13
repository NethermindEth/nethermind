// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning;

internal class NullCommitter : ICommitter, IBlockCommitter
{
    public static NullCommitter Instance = new NullCommitter();

    private NullCommitter()
    {
    }

    public void Dispose() { }

    public void CommitNode(ref TreePath path, NodeCommitInfo nodeCommitInfo) { }
}
