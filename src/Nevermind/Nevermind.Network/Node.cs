using Nevermind.Core.Crypto;

namespace Nevermind.Network
{
    public class Node
    {
        public int Port { get; set; }
        public string Host { get; set; }
        public PublicKey PublicKey { get; set; }
        
        // capabilities and client id here?
    }
}