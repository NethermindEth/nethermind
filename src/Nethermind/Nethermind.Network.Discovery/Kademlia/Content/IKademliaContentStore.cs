// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery.Kademlia.Content;

public interface IKademliaContentStore<in TContentKey, TContent>
{
    bool TryGetValue(TContentKey hash, out TContent? value);
}
