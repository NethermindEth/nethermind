using System;
using System.Collections.Generic;
using System.IO;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace Nevermind.Network.Rlpx
{
    public class FrameEncryptionProcessor : MessageProcessorBase<byte[], byte[]>
    {
        private readonly IBufferedCipher _decryptionCipher;
        private readonly KeccakDigest _egressMac;
        private readonly IBufferedCipher _encryptionCipher;
        private readonly KeccakDigest _ingressMac;
        private readonly byte[] _macSecret;

        public FrameEncryptionProcessor(EncryptionSecrets secrets)
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

        public override void ToRight(byte[] input, IList<byte[]> output)
        {
            throw new NotImplementedException();
        }

        public override void ToLeft(byte[] input, IList<byte[]> output)
        {
            _encryptionCipher.ProcessBytes(input, 0, 16, input, 0);

            UpdateMac(_egressMac, input, 0, input, 16, true);

            // header-mac: right128 of egress-mac.update(aes(mac-secret,egress-mac) ^ header-ciphertext).digest
            _encryptionCipher.ProcessBytes(input, 32, input.Length - 48, input, 32);
            _egressMac.BlockUpdate(input, 32, input.Length - 48);

            // frame-mac: right128 of egress-mac.update(aes(mac-secret,egress-mac) ^ right128(egress-mac.update(frame-ciphertext).digest))
            byte[] frameMac = new byte[_egressMac.GetDigestSize()];
            DoFinalNoReset(_egressMac, frameMac, 0); // fmacseed
            UpdateMac(_egressMac, frameMac, 0, frameMac, 0, true);
            
            Buffer.BlockCopy(frameMac, 0, input, input.Length - 16, 16);
            output.Add(input);
        }

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

        private void DoFinalNoReset(KeccakDigest mac, byte[] output, int offset)
        {
            new KeccakDigest(mac).DoFinal(output, offset);
        }

        private AesFastEngine MakeMacCipher()
        {
            AesFastEngine aesFastEngine = new AesFastEngine();
            aesFastEngine.Init(true, new KeyParameter(_macSecret));
            return aesFastEngine;
        }
    }
}