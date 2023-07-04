// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;

using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;

namespace Nethermind.Network.Rlpx
{
    /// <summary>
    /// partially adapted from ethereumJ
    /// </summary>
    public sealed class FrameMacProcessor : IFrameMacProcessor
    {
        private readonly PublicKey _remoteNodeId;
        private readonly KeccakHash _egressMac;
        private readonly KeccakHash _ingressMac;
        private readonly KeccakHash _egressMacCopy;
        private readonly KeccakHash _ingressMacCopy;
        private readonly IBlockCipher _aesEngine;
        private readonly byte[] _macSecret;

        public FrameMacProcessor(PublicKey remoteNodeId, EncryptionSecrets secrets)
        {
            _remoteNodeId = remoteNodeId;
            _macSecret = secrets.MacSecret;
            _egressMac = secrets.EgressMac;
            _egressMacCopy = _egressMac.Copy();
            _ingressMac = secrets.IngressMac;
            _ingressMacCopy = _ingressMac.Copy();
            _aesEngine = MakeMacCipher();
            _checkMacBuffer = new byte[_ingressMac.HashSize];
            _addMacBuffer = new byte[_ingressMac.HashSize];
            _ingressAesBlockBuffer = new byte[_ingressMac.HashSize];
            _egressAesBlockBuffer = new byte[_ingressMac.HashSize];
        }

        private IBlockCipher MakeMacCipher()
        {
            IBlockCipher aesFastEngine = AesEngineX86Intrinsic.IsSupported ? new AesEngineX86Intrinsic() : new AesEngine();
            aesFastEngine.Init(true, new KeyParameter(_macSecret));
            return aesFastEngine;
        }

        public void AddMac(byte[] input, int offset, int length, bool isHeader)
        {
            if (isHeader)
            {
                input.AsSpan(0, 32).CopyTo(_addMacBuffer);
                UpdateMac(_egressMac, _egressMacCopy, _addMacBuffer, offset, input, offset + length, true); // TODO: confirm header is seed
            }
            else
            {
                _egressMac.Update(input.AsSpan(offset, length));

                // frame-mac: right128 of egress-mac.update(aes(mac-secret,egress-mac) ^ right128(egress-mac.update(frame-ciphertext).digest))
                DoFinalNoReset(_egressMac, _egressMacCopy, _addMacBuffer); // frame MAC seed
                UpdateMac(_egressMac, _egressMacCopy, _addMacBuffer, 0, input, offset + length, true);
            }
        }

        public void UpdateEgressMac(byte[] input)
        {
            _egressMac.Update(input);
        }

        public void UpdateIngressMac(byte[] input, bool isHeader)
        {
            if (isHeader)
            {
                input.AsSpan().CopyTo(_checkMacBuffer.AsSpan(0, 16));
            }
            else
            {
                _ingressMac.Update(input);
            }
        }

        public void CalculateMac(byte[] output)
        {
            DoFinalNoReset(_egressMac, _egressMacCopy, _addMacBuffer); // frame MAC seed
            UpdateMac(_egressMac, _egressMacCopy, _addMacBuffer, 0, output, 0, true);
        }

        public void AddMac(byte[] input, int offset, int length, byte[] output, int outputOffset, bool isHeader)
        {
            if (isHeader)
            {
                input.AsSpan(0, 16).CopyTo(_addMacBuffer);
                UpdateMac(_egressMac, _egressMacCopy, _addMacBuffer, offset, output, outputOffset, true); // TODO: confirm header is seed
            }
            else
            {
                _egressMac.Update(input.AsSpan(offset, length));

                // frame-mac: right128 of egress-mac.update(aes(mac-secret,egress-mac) ^ right128(egress-mac.update(frame-ciphertext).digest))
                DoFinalNoReset(_egressMac, _egressMacCopy, _addMacBuffer); // frame MAC seed
                UpdateMac(_egressMac, _egressMacCopy, _addMacBuffer, 0, output, outputOffset, true);
            }
        }

        private byte[] _addMacBuffer;
        private byte[] _checkMacBuffer;
        private byte[] _ingressAesBlockBuffer;
        private byte[] _egressAesBlockBuffer;

        public bool CheckMac(byte[] mac, bool isHeader)
        {
            if (!isHeader)
            {
                DoFinalNoReset(_ingressMac, _ingressMacCopy, _checkMacBuffer); // frame MAC seed
            }

            byte[] aesBlock = _ingressAesBlockBuffer;
            DoFinalNoReset(_ingressMac, _ingressMacCopy, aesBlock);

            _aesEngine.ProcessBlock(aesBlock, 0, aesBlock, 0);

            // Note that although the mac digest size is 32 bytes, we only use 16 bytes in the computation
            int length = 16;
            for (int i = 0; i < length; i++)
            {
                aesBlock[i] ^= _checkMacBuffer[i];
            }

            _ingressMac.Update(aesBlock.AsSpan(0, length));
            byte[] result = _checkMacBuffer;
            DoFinalNoReset(_ingressMac, _ingressMacCopy, result);

            bool isMacSame = true;
            for (int i = 0; i < length; i++)
            {
                if (mac[i] != result[i])
                {
                    isMacSame = false;
                    break;
                }
            }

            return isMacSame;
        }

        public void CheckMac(byte[] input, int offset, int length, bool isHeader)
        {
            if (isHeader)
            {
                input.AsSpan(0, 32).CopyTo(_checkMacBuffer);
                UpdateMac(_ingressMac, _ingressMacCopy, _checkMacBuffer, offset, input, offset + length, false);
            }
            else
            {
                _ingressMac.Update(input.AsSpan(offset, length));

                // frame-mac: right128 of egress-mac.update(aes(mac-secret,egress-mac) ^ right128(egress-mac.update(frame-ciphertext).digest))
                DoFinalNoReset(_ingressMac, _ingressMacCopy, _checkMacBuffer); // frame MAC seed
                UpdateMac(_ingressMac, _ingressMacCopy, _checkMacBuffer, 0, input, offset + length, false);
            }
        }


        /// <summary>
        /// adapted from ethereumJ
        /// </summary>
        private void UpdateMac(KeccakHash mac, KeccakHash macCopy, byte[] seed, int offset, byte[] output, int outOffset, bool egress)
        {
            byte[] aesBlock = egress ? _egressAesBlockBuffer : _ingressAesBlockBuffer;
            DoFinalNoReset(mac, macCopy, aesBlock);

            _aesEngine.ProcessBlock(aesBlock, 0, aesBlock, 0);

            // Note that although the mac digest size is 32 bytes, we only use 16 bytes in the computation
            int length = 16;
            for (int i = 0; i < length; i++)
            {
                aesBlock[i] ^= seed[i + offset];
            }

            mac.Update(aesBlock.AsSpan(0, length));
            byte[] result = seed;
            DoFinalNoReset(mac, macCopy, result);

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
        }

        private void DoFinalNoReset(KeccakHash mac, KeccakHash macCopy, byte[] output)
        {
            macCopy.ResetTo(mac);
            macCopy.UpdateFinalTo(output);
        }

        public void Dispose()
        {
            _egressMacCopy.Reset();
            _ingressMacCopy.Reset();
        }
    }
}
