// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.ProtocolHandlers;

namespace Nethermind.DataMarketplace.Subprotocols.Factories
{
    public class NullNdmSubprotocolFactory : INdmSubprotocolFactory
    {
        public IProtocolHandler Create(ISession p2PSession)
        {
            return NullProtocolHandler.Instance;
        }

        public void ChangeConsumerAddress(Address address)
        {
        }

        public void ChangeProviderAddress(Address address)
        {
        }
    }
}
