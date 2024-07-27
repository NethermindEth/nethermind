// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;
using Nethermind.Core.Crypto;
using Nethermind.Network.Discovery.Kademlia;

namespace Nethermind.Network.Discovery.Portal;

public class NoopStore : IKademlia<IEnr, ContentKey, ContentContent>.IStore
{
    public bool TryGetValue(ContentKey hash, out ContentContent? value)
    {
        value = null;
        return false;
    }
}
