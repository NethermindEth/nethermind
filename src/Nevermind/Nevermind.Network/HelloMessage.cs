using System.Collections.Generic;
using Nevermind.Core.Crypto;

namespace Nevermind.Network
{
    public class HelloMessage : P2PMessage
    {
        public int P2PVersion { get; set; }
        public string ClientId { get; set; }
        public Dictionary<Capability, int> Capabilities { get; set; }
        public int ListenPort { get; set; }
        public PublicKey NodeId { get; set; }
        public override int MessageId => MessageCode.Hello;
    }
}