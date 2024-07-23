// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;
using Nethermind.Network.Discovery.Kademlia;

namespace Nethermind.Network.Discovery.Portal;

public class NoopStore : IKademlia<IEnr, byte[]>.IStore
{
    public bool TryGetValue(IEnr hash, out byte[] value)
    {
        value = null!;
        return false;
    }
}
