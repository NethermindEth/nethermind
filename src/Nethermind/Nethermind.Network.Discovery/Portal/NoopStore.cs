// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;
using Nethermind.Core.Crypto;
using Nethermind.Network.Discovery.Kademlia;

namespace Nethermind.Network.Discovery.Portal;

public class NoopStore : IKademlia<IEnr>.IStore
{
    public bool TryGetValue(ValueHash256 hash, out byte[] value)
    {
        value = null!;
        return false;
    }
}
