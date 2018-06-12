using System;

namespace Nethermind.Network.P2P
{
    public class DisconnectEventArgs : EventArgs
    {
        public DisconnectReason DisconnectReason { get; set; }
        public DisconnectType DisconnectType { get; set; }
        public string SessionId { get; set; }

        public DisconnectEventArgs(DisconnectReason disconnectReason, DisconnectType disconnectType, string sessionId)
        {
            DisconnectReason = disconnectReason;
            DisconnectType = disconnectType;
            SessionId = sessionId;
        }
    }
}