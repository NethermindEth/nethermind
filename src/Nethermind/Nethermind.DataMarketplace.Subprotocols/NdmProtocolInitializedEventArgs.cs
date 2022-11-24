// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Network.P2P.ProtocolHandlers;

namespace Nethermind.DataMarketplace.Subprotocols
{
    public class NdmProtocolInitializedEventArgs : ProtocolInitializedEventArgs
    {
        public string? Protocol { get; set; }
        public byte? ProtocolVersion { get; set; }
        public Address? ProviderAddress { get; set; }
        public Address? ConsumerAddress { get; set; }

        public NdmProtocolInitializedEventArgs(IProtocolHandler subprotocol) : base(subprotocol)
        {
        }
    }
}
