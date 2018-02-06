using Nevermind.Core.Crypto;

namespace Nevermind.Network
{
    public class AuthMessageBase : MessageBase
    {
        public Signature Signature { get; set; }
        public PublicKey PublicKey { get; set; }
        public byte[] Nonce { get; set; }
        public int Version { get; set; } = 4;
    }
}