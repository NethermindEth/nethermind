using System;

namespace Nethermind.Network.P2P
{
    public class ProtocolEventArgs : EventArgs
    {
        public int Version { get; }
        public string ProtocolCode { get; }

        public ProtocolEventArgs(string protocolCode, int version)
        {
            Version = version;
            ProtocolCode = protocolCode;
        }   
    }
}