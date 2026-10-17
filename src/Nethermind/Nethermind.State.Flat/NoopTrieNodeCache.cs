// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Nethermind.Trie.NodeLog;

namespace Nethermind.State.Flat;

/// <summary>An <see cref="ITrieNodeCache"/> that caches nothing.</summary>
public class NoopTrieNodeCache : ITrieNodeCache
{
    public bool TryGet(Hash256? address, in TreePath path, in ValueHash256 hash, out NodeRecordRef record)
    {
        record = default;
        return false;
    }

    public void Add(TransientResource transientResource)
    {
        transientResource.WaitForExclusiveLease();
        transientResource.PromoteNativeWarmedNodes();
    }

    public void Clear() { }
}
