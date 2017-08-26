using System;
using Org.BouncyCastle.Asn1.Sec;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Utilities.Encoders;

namespace Nevermind.Core
{
    public static class Signer
    {
        public static void Sign(Transaction transaction, PrivateKey privateKey)
        {
            byte[] hash = Keccak.Compute(transaction.Collapse());
            throw new NotImplementedException();
        }

        public static Address Recover(Transaction transaction)
        {
            PublicKey publicKey = new PublicKey(new byte[62]);
            return publicKey.Address;
        }
    }
}
