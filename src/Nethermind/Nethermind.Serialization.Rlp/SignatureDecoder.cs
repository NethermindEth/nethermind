// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Serialization.Rlp
{
    public static class SignatureDecoder
    {
        public static Signature DecodeSignature(RlpStream rlpStream)
        {
            ReadOnlySpan<byte> vBytes = rlpStream.DecodeByteArraySpan();
            ReadOnlySpan<byte> rBytes = rlpStream.DecodeByteArraySpan();
            ReadOnlySpan<byte> sBytes = rlpStream.DecodeByteArraySpan();

            if (vBytes[0] == 0 || rBytes[0] == 0 || sBytes[0] == 0)
            {
                throw new RlpException("VRS starting with 0");
            }

            if (rBytes.Length > 32 || sBytes.Length > 32)
            {
                throw new RlpException("R and S lengths expected to be less or equal 32");
            }

            ulong v = vBytes.ReadEthUInt64();

            if (rBytes.SequenceEqual(Bytes.Zero32) && sBytes.SequenceEqual(Bytes.Zero32))
            {
                throw new RlpException("Both 'r' and 's' are zero when decoding a transaction.");
            }

            Signature signature = new(rBytes, sBytes, v);
            return signature;
        }
    }
}
