using Nevermind.Core.Crypto;

namespace Nevermind.Network
{
    public class EncryptionSecrets
    {
        public byte[] EgressMac { get; set; }
        public byte[] IngressMac { get; set; }
        public byte[] AesSecret { get; set; } // TODO: is it sha3 or keccak
        public byte[] MacSecret { get; set; } // TODO: is it sha3 or keccak
        public byte[] Token { get; set; } // TODO: is it sha3 or keccak
    }
}