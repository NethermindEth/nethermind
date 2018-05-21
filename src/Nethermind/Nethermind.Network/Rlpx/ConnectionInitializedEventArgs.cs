using System;
using Nethermind.Network.P2P;

namespace Nethermind.Network.Rlpx
{
    public class ConnectionInitializedEventArgs : EventArgs
    {
        public IP2PSession Session { get; set; }

        public ConnectionInitializedEventArgs(IP2PSession session)
        {
            Session = session;
        }
    }
}