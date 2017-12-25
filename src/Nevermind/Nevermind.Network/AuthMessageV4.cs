using Nevermind.Core.Crypto;
using Nevermind.Core.Encoding;
using Nevermind.Core.Extensions;

namespace Nevermind.Network
{
    public class AuthMessageV4
    {
        public Signature Signature { get; set; }
        public PublicKey PublicKey { get; set; }
        public byte[] Nonce { get; set; }
        public int Version { get; private set; } = 4;

        public static AuthMessageV4 Decode(byte[] data)
        {
            Rlp rlp = new Rlp(data);
            object[] decodedRaw = (object[])Rlp.Decode(rlp);
            AuthMessageV4 authMessage = new AuthMessageV4();
            Signature signature = new Signature((byte[])decodedRaw[0]);
            authMessage.Signature = signature;
            authMessage.PublicKey = new PublicKey((byte[])decodedRaw[1]);
            authMessage.Nonce = (byte[])decodedRaw[2];
            authMessage.Version = ((byte[])decodedRaw[3]).ToInt32();
            return authMessage;
        }

        public static byte[] Encode(AuthMessageV4 authMessage)
        {
            return Rlp.Encode(
                Rlp.Encode(Bytes.Concat(authMessage.Signature.Bytes, authMessage.Signature.V)),
                Rlp.Encode(authMessage.PublicKey.PrefixedBytes.Slice(1, 64)),
                Rlp.Encode(authMessage.Nonce),
                Rlp.Encode(authMessage.Version)
            ).Bytes;
        }
    }
}