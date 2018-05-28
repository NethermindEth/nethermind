using System.Collections.Generic;
using Nethermind.Network.P2P.Subprotocols.Eth;

namespace Nethermind.Network.P2P
{
    public class P2PProtocolInitializedEventArgs : ProtocolInitializedEventArgs
    {
        public byte P2PVersion { get; set; }
        public string ClientId { get; set; }
        public List<Capability> Capabilities { get; set; }

        public P2PProtocolInitializedEventArgs(P2PProtocolHandler protocolHandler) : base(protocolHandler)
        {
        }
    }
}