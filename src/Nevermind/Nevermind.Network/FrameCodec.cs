using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Nevermind.Core.Encoding;
using Nevermind.Core.Extensions;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace Nevermind.Network
{
    /// <summary>
    ///     Code adapted from ethereumJ (https://github.com/ethereum/ethereumj)
    /// </summary>
    public class FrameCodec : IFrameCodec
    {
        public const int FrameBoundary = 16;
        public const int MaxFrameSize = FrameBoundary * 64;
        private readonly IBufferedCipher _decryptionCipher;
        private readonly KeccakDigest _egressMac;
        private readonly IBufferedCipher _encryptionCipher;
        private readonly KeccakDigest _ingressMac;
        private readonly byte[] _macSecret;

        public FrameCodec(EncryptionSecrets secrets)
        {
            _macSecret = secrets.MacSecret;

            // TODO: check, EthereumJ suggest a block size of 32 bytes while AES should have a 16 bytes block size
            _encryptionCipher = CipherUtilities.GetCipher("AES/CTR/NoPadding");
            _encryptionCipher.Init(true, new ParametersWithIV(ParameterUtilities.CreateKeyParameter("AES", secrets.AesSecret), new byte[16]));

            _decryptionCipher = CipherUtilities.GetCipher("AES/CTR/NoPadding");
            _encryptionCipher.Init(false, new ParametersWithIV(ParameterUtilities.CreateKeyParameter("AES", secrets.AesSecret), new byte[16]));

            _egressMac = secrets.EgressMac;
            _ingressMac = secrets.IngressMac;
        }

        public FrameCodec(byte[] macSecret)
        {
            _macSecret = macSecret;
        }

        public byte[] Write(int protocolType, int packetType, byte[] data)
        {
            return Write(protocolType, packetType, null, data);
        }

        public byte[] Write(int protocolType, int packetType, int? contextId, byte[] data)
        {
            byte[] padded = Pad16(data);
            int framesCount = padded.Length / MaxFrameSize + 1;
            if (framesCount > 1)
            {
                Debug.Assert(contextId.HasValue, "Context ID expected when in multi-frame packet");
            }

            byte[][] chunks = new byte[framesCount][];
            byte[] packetTypeData = Rlp.Encode(packetType).Bytes;
            for (int i = 0; i < framesCount; i++)
            {
                int offset = MaxFrameSize * i;
                int dataSize = Math.Min(MaxFrameSize, padded.Length - offset);
                byte[] frame = padded.Slice(offset, dataSize);
                if (i == 0)
                {
                    frame = Bytes.Concat(packetTypeData, frame);
                }

                byte[] header = new byte[16];
                header[0] = (byte)(frame.Length >> 16);
                header[1] = (byte)(frame.Length >> 8);
                header[2] = (byte)frame.Length;
                List<object> headerDataItems = new List<object>();
                headerDataItems.Add(protocolType);
                if (framesCount > 1)
                {
                    headerDataItems.Add(contextId.Value);
                    if (i == 0)
                    {
                        headerDataItems.Add(packetTypeData.Length + padded.Length);
                    }
                }

                // TODO: ethereumJ using buffered read here (etheruemJ only handling one frame per packet)
                byte[] headerDataBytes = Rlp.Encode(headerDataItems).Bytes;
                Buffer.BlockCopy(headerDataBytes, 0, header, 3, headerDataBytes.Length);

                _encryptionCipher.ProcessBytes(header, 0, 16, header, 0);

                // TODO: refactor UpdateMac

                // header-mac: right128 of egress-mac.update(aes(mac-secret,egress-mac) ^ header-ciphertext).digest
                byte[] headerMac = new byte[16];
                UpdateMac(_egressMac, header, 0, headerMac, 0, true);

                _encryptionCipher.ProcessBytes(frame, frame.Length, 0, frame, 0);
                _egressMac.BlockUpdate(frame, 0, frame.Length);

                // frame-mac: right128 of egress-mac.update(aes(mac-secret,egress-mac) ^ right128(egress-mac.update(frame-ciphertext).digest))
                byte[] frameMac = new byte[_egressMac.GetDigestSize()];
                DoFinalNoReset(_egressMac, frameMac); // fmacseed
                UpdateMac(_egressMac, frameMac, 0, frameMac, 0, true);

                chunks[i] = Bytes.Concat(
                    header,
                    headerMac,
                    frame,
                    frameMac.Slice(0, 16));
            }

            return Bytes.Concat(chunks);
        }

        private byte[] UpdateMac(KeccakDigest mac, byte[] seed, int offset, byte[] output, int outOffset, bool egress)
        {
            byte[] aesBlock = new byte[mac.GetDigestSize()];
            DoFinalNoReset(mac, aesBlock);

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
            DoFinalNoReset(mac, result);

            if (egress)
            {
                Array.Copy(result, 0, output, outOffset, length);
            }
            else
            {
                for (int i = 0; i < length; i++)
                {
                    if (output[i + outOffset] != result[i])
                    {
                        throw new IOException("MAC mismatch");
                    }
                }
            }

            return result;
        }

        private void DoFinalNoReset(KeccakDigest mac, byte[] output)
        {
            new KeccakDigest(mac).DoFinal(output, 0);
        }

        private AesFastEngine MakeMacCipher()
        {
            AesFastEngine aesFastEngine = new AesFastEngine();
            aesFastEngine.Init(true, new KeyParameter(_macSecret));
            return aesFastEngine;
        }

        private static byte[] Pad16(byte[] data)
        {
            int paddingSize = 16 - data.Length % 16;
            byte[] padded = paddingSize == 16 ? data : Bytes.Concat(data, new byte[paddingSize]);
            return padded;
        }
    }
}