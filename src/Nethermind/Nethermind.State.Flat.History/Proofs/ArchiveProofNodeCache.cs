// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;

namespace Nethermind.State.Flat.History.Proofs;

internal sealed class ArchiveProofNodeCache(int capacity)
{
    private readonly LruCache<ValueHash256, byte[]> _nodes = new(capacity, nameof(ArchiveProofNodeCache));

    public bool TryGet(in ValueHash256 hash, out byte[]? rlp)
    {
        lock (_nodes)
        {
            return _nodes.TryGet(hash, out rlp);
        }
    }

    public void Set(in ValueHash256 hash, byte[] rlp)
    {
        lock (_nodes)
        {
            _nodes.Set(hash, rlp);
        }
    }
}
