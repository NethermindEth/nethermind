using System.Collections.Generic;

namespace Nevermind.Network
{
    public class HelloMessage : P2PMessage
    {
        public int P2PVersion { get; set; }
        public string ClientId { get; set; }
        public Dictionary<Capability, int> Capabilities { get; set; }
        public int ListenPort { get; set; }
        public NodePublicKey NodeId { get; set; }
        public override int MessageId => MessageCode.Hello;
    }
}