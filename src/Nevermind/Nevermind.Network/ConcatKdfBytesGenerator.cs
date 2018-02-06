/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Diagnostics;
using Nevermind.Core.Extensions;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;

namespace Nevermind.Network
{
    /// <summary>
    ///     Code adapted from ethereumJ (https://github.com/ethereum/ethereumj)
    ///     Basic KDF generator for derived keys and ivs as defined by NIST SP 800-56A.
    /// </summary>
    public class ConcatKdfBytesGenerator : IDerivationFunction
    {
        private readonly int _counterStart;
        private byte[] _iv;
        private byte[] _shared;

        protected ConcatKdfBytesGenerator(int counterStart, IDigest digest)
        {
            _counterStart = counterStart;
            Digest = digest;
        }

        public ConcatKdfBytesGenerator(IDigest digest)
            : this(1, digest)
        {
        }

        public void Init(IDerivationParameters param)
        {
            if (param is KdfParameters kdfParameters)
            {
                _shared = kdfParameters.GetSharedSecret();
                _iv = kdfParameters.GetIV();
            }
            else if (param is Iso18033KdfParameters iso18033KdfParameters)
            {
                _shared = iso18033KdfParameters.GetSeed();
                _iv = null;
            }
            else
            {
                throw new ArgumentException("KDF parameters required for KDF2Generator");
            }
        }

        /**
         * fill len bytes of the output buffer with bytes generated from the
         * derivation function.
         * 
         * @throws IllegalArgumentException
         *             if the size of the request will cause an overflow.
         * @throws DataLengthException
         *             if the out buffer is too small.
         */
        public int GenerateBytes(byte[] output, int outOff, int length)
        {
            if (output.Length - length < outOff)
            {
                throw new DataLengthException("output buffer too small");
            }

            long oBytes = length;
            int outLen = Digest.GetDigestSize();

            //
            // this is at odds with the standard implementation, the
            // maximum value should be hBits * (2^32 - 1) where hBits
            // is the digest output size in bits. We can't have an
            // array with a long index at the moment...
            //
            if (oBytes > (2L << 32) - 1)
            {
                throw new ArgumentException("Output length too large");
            }

            int cThreshold = (int)((oBytes + outLen - 1) / outLen);

            byte[] digest = new byte[Digest.GetDigestSize()];

//            byte[] C = new byte[4];
//            Pack.intToBigEndian(counterStart, C, 0);
            byte[] c = _counterStart.ToBigEndianByteArray();
            Debug.Assert(c.Length == 4, "need to be 4 bytes long");

            int counterBase = _counterStart & ~0xFF;

            for (int i = 0; i < cThreshold; i++)
            {
                Digest.BlockUpdate(c, 0, c.Length);
                Digest.BlockUpdate(_shared, 0, _shared.Length);

                if (_iv != null)
                {
                    Digest.BlockUpdate(_iv, 0, _iv.Length);
                }

                Digest.DoFinal(digest, 0);

                if (length > outLen)
                {
                    Array.Copy(digest, 0, output, outOff, outLen);
                    outOff += outLen;
                    length -= outLen;
                }
                else
                {
                    Array.Copy(digest, 0, output, outOff, length);
                }

                if (++c[3] == 0)
                {
                    counterBase += 0x100;
//                    Pack.intToBigEndian(counterBase, C, 0);
                    c = counterBase.ToBigEndianByteArray();
                    Debug.Assert(c.Length == 4, "need to be 4 bytes long");
                }
            }

            Digest.Reset();

            return (int)oBytes;
        }

        public IDigest Digest { get; }
    }
}