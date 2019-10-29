using System;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;

namespace Nethermind.Core
{
    public static class SignatureDecoder
    {
        public static Signature DecodeSignature(RlpStream rlpStream)
        {
            Span<byte> vBytes = rlpStream.DecodeByteArraySpan();
            Span<byte> rBytes = rlpStream.DecodeByteArraySpan();
            Span<byte> sBytes = rlpStream.DecodeByteArraySpan();

            if (vBytes[0] == 0 || rBytes[0] == 0 || sBytes[0] == 0)
            {
                throw new RlpException("VRS starting with 0");
            }

            if (rBytes.Length > 32 || sBytes.Length > 32)
            {
                throw new RlpException("R and S lengths expected to be less or equal 32");
            }

            int v = vBytes.ReadEthInt32();

            if (rBytes.SequenceEqual(Bytes.Zero32) && sBytes.SequenceEqual(Bytes.Zero32))
            {
                throw new RlpException("Both 'r' and 's' are zero when decoding a transaction.");
            }

            Signature signature = new Signature(rBytes, sBytes, v);
            return signature;
        }
    }
}