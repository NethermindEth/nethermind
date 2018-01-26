using Nevermind.Core.Crypto;

namespace Nevermind.Network
{
    public class EncryptionSecrets
    {
        public Keccak EgressMac { get; set; }
        public Keccak IngressMac { get; set; }
    }
}