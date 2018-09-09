using System;
using Nethermind.Stats;
using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P
{
    public class DisconnectEventArgs : EventArgs
    {
        public DisconnectReason DisconnectReason { get; set; }
        public DisconnectType DisconnectType { get; set; }

        public DisconnectEventArgs(DisconnectReason disconnectReason, DisconnectType disconnectType)
        {
            DisconnectReason = disconnectReason;
            DisconnectType = disconnectType;
        }
    }
}