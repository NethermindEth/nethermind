// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


using Nethermind.Network.Discovery.Kademlia;

namespace Nethermind.Network.Discovery.Discv4;

internal class LruCache<THash, T> where THash : struct, IKademiliaHash<THash>
{
    private int v1;
    private string v2;

    public LruCache(int v1, string v2)
    {
        this.v1 = v1;
        this.v2 = v2;
    }

    internal void Delete(THash key)
    {
        throw new NotImplementedException();
    }

    internal void Set(THash key, T now)
    {
        throw new NotImplementedException();
    }

    internal bool TryGet(THash key, out T? lastAttempt)
    {
        lastAttempt = default;
        throw new NotImplementedException();
    }
}
