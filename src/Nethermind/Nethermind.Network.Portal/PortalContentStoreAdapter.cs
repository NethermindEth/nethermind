// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.Kademlia.Content;
using Nethermind.Network.Portal.Messages;

namespace Nethermind.Network.Portal;

/// <summary>
/// Adapter from IPortalContentNetworkStore to Kademlia's store.
/// </summary>
/// <param name="sourceStore"></param>
public class PortalContentKademliaContentStoreAdapter(IPortalContentNetworkStore sourceStore) : IKademliaContentStore<byte[], LookupContentResult>
{
    public bool TryGetValue(byte[] contentId, out LookupContentResult? value)
    {
        var sourceContent = sourceStore.GetContent(contentId);
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
