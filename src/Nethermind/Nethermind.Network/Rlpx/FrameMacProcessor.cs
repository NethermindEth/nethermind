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
using System.IO;
using Nethermind.Core.Crypto;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;

namespace Nethermind.Network.Rlpx
{
    /// <summary>
    /// partially adapted from ethereumJ
    /// </summary>
    public class FrameMacProcessor : IFrameMacProcessor
    {
        private readonly PublicKey _remoteNodeId;
        private readonly KeccakDigest _egressMac;
        private readonly KeccakDigest _ingressMac;
        private readonly byte[] _macSecret;

        // TODO: three arguments in place of secrets
        public FrameMacProcessor(PublicKey remoteNodeId, EncryptionSecrets secrets)
        {
            _remoteNodeId = remoteNodeId;
            _macSecret = secrets.MacSecret;
            _egressMac = secrets.EgressMac;
            _ingressMac = secrets.IngressMac;
        }

        public void AddMac(byte[] input, int offset, int length, bool isHeader)
        {
            if (isHeader)
            {
                UpdateMac(_egressMac, input, offset, input, offset + length, true); // TODO: confirm header is seed 
            }
            else
            {
                _egressMac.BlockUpdate(input, offset, length);

                // frame-mac: right128 of egress-mac.update(aes(mac-secret,egress-mac) ^ right128(egress-mac.update(frame-ciphertext).digest))
                byte[] buffer = new byte[_egressMac.GetDigestSize()];
                DoFinalNoReset(_egressMac, buffer, 0); // frame MAC seed
                UpdateMac(_egressMac, buffer, 0, input, offset + length, true);
            }
        }

        public void CheckMac(byte[] input, int offset, int length, bool isHeader)
        {
            if (isHeader)
            {
                UpdateMac(_ingressMac, input, offset, input, offset + length, false); // TODO: confirm header is seed 
            }
            else
            {
                _ingressMac.BlockUpdate(input, offset, length);

                // frame-mac: right128 of egress-mac.update(aes(mac-secret,egress-mac) ^ right128(egress-mac.update(frame-ciphertext).digest))
                byte[] buffer = new byte[_ingressMac.GetDigestSize()];
                DoFinalNoReset(_ingressMac, buffer, 0); // frame MAC seed
                UpdateMac(_ingressMac, buffer, 0, input, offset + length, false);
            }
        }


        /// <summary>
        /// adapted from ethereumJ
        /// </summary>
        private byte[] UpdateMac(KeccakDigest mac, byte[] seed, int offset, byte[] output, int outOffset, bool egress)
        {
            byte[] aesBlock = new byte[mac.GetDigestSize()];
            DoFinalNoReset(mac, aesBlock, 0);

            // TODO: check if need to make a new one each time
            MakeMacCipher().ProcessBlock(aesBlock, 0, aesBlock, 0);

            // Note that although the mac digest size is 32 bytes, we only use 16 bytes in the computation
            int length = 16;
            for (int i = 0; i < length; i++)
            {
                aesBlock[i] ^= seed[i + offset];
            }

            mac.BlockUpdate(aesBlock, 0, length);

            byte[] result = new byte[mac.GetDigestSize()];
            DoFinalNoReset(mac, result, 0);

            if (egress)
            {
                Array.Copy(result, 0, output, outOffset, length);
            }
            else
            {
                bool isMacSame = true;
                for (int i = 0; i < length; i++)
                {
                    if (output[i + outOffset] != result[i])
                    {
                        isMacSame = false;
                        break;
                    }
                }

                if (!isMacSame)
                {
                   throw new IOException($"MAC mismatch from {_remoteNodeId}");
                }
            }

            return result;
        }

        private void DoFinalNoReset(KeccakDigest mac, byte[] output, int offset)
        {
            new KeccakDigest(mac).DoFinal(output, offset);
        }

        private AesEngine MakeMacCipher()
        {
            AesEngine aesFastEngine = new AesEngine();
            aesFastEngine.Init(true, new KeyParameter(_macSecret));
            return aesFastEngine;
        }
    }
}