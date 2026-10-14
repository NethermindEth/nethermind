// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Trie.NodeLog;

/// <summary>Records of the nodes of one trie that the trie node log does not hold.</summary>
internal interface INodeRecordSource
{
    /// <returns>False when the source has no node of <paramref name="hash"/> at <paramref name="path"/>.</returns>
    bool TryLoad(in TreePath path, in ValueHash256 hash, out NodeRecordRef record);
}
