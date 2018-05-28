using System;

namespace Nethermind.Network.P2P
{
    public class ProtocolInitializedEventArgs : EventArgs
    {
        public IProtocolHandler ProtocolHandler { get; set; }

        public ProtocolInitializedEventArgs(IProtocolHandler protocolHandler)
        {
            ProtocolHandler = protocolHandler;
        }
    }
}