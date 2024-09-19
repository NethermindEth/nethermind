// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Network.Discovery.Portal.Messages;

namespace Nethermind.Network.Discovery.Portal;

/// <summary>
/// Adapter from IPortalContentNetworkStore to Kademlia's store.
/// </summary>
/// <param name="sourceStore"></param>
public class PortalContentStoreAdapter(IPortalContentNetworkStore sourceStore) : IKademliaContent<IEnr, byte[], LookupContentResult>.IStore
{
    public bool TryGetValue(byte[] contentId, out LookupContentResult? value)
    {
        byte[]? sourceContent = sourceStore.GetContent(contentId);
        if (sourceContent == null)
        {
            value = null;
            return false;
        }

        value = new LookupContentResult()
        {
            Payload = sourceContent
        };
        return true;
    }
}
