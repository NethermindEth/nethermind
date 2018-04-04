using Nethermind.Core.Crypto;

namespace Nethermind.Core
{
    public class Bootnode
    {
        public Bootnode(Hex publicKey, string ip, int port, string description)
        {
            PublicKey = new PublicKey(publicKey);
            Host = ip;
            Port = port;
            Description = description;
        }

        public PublicKey PublicKey { get; set; }
        public string Host { get; set; }
        public int Port { get; set; }
        public string Description { get; set; }
    }
}