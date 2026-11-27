// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;

namespace Nethermind.Network.Rlpx;

/// <summary>
/// partially adapted from ethereumJ
/// </summary>
public sealed class FrameMacProcessor : IFrameMacProcessor
{
    private readonly KeccakHash _egressMac;
    private readonly KeccakHash _ingressMac;
    private readonly KeccakHash _egressMacCopy;
    private readonly KeccakHash _ingressMacCopy;
    private readonly IBlockCipher _aesEngine;
    private readonly byte[] _macSecret;

    public FrameMacProcessor(EncryptionSecrets secrets)
    {
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
        IBlockCipher aesFastEngine = AesUtilities.CreateEngine();
        aesFastEngine.Init(true, new KeyParameter(_macSecret));
        return aesFastEngine;
    }

    public void UpdateEgressMac(ReadOnlySpan<byte> input) => _egressMac.Update(input);

    public void UpdateIngressMac(ReadOnlySpan<byte> input, bool isHeader)
    {
        if (isHeader)
        {
            input.CopyTo(_checkMacBuffer.AsSpan(0, 16));
        }
        else
        {
            _ingressMac.Update(input);
        }
    }

    public void CalculateMac(Span<byte> mac)
    {
        DoFinalNoReset(_egressMac, _egressMacCopy, _addMacBuffer); // frame MAC seed
        UpdateMac(_egressMac, _egressMacCopy, _addMacBuffer, mac);
    }

    public void AddMac(ReadOnlySpan<byte> input, Span<byte> mac, bool isHeader)
    {
        if (isHeader)
        {
            input[..16].CopyTo(_addMacBuffer);
            UpdateMac(_egressMac, _egressMacCopy, _addMacBuffer, mac); // TODO: confirm header is seed
        }
        else
        {
            _egressMac.Update(input);

            // frame-mac: right128 of egress-mac.update(aes(mac-secret,egress-mac) ^ right128(egress-mac.update(frame-ciphertext).digest))
            DoFinalNoReset(_egressMac, _egressMacCopy, _addMacBuffer); // frame MAC seed
            UpdateMac(_egressMac, _egressMacCopy, _addMacBuffer, mac);
        }
    }

    private readonly byte[] _addMacBuffer;
    private readonly byte[] _checkMacBuffer;
    private readonly byte[] _ingressAesBlockBuffer;
    private readonly byte[] _egressAesBlockBuffer;

    public bool CheckMac(ReadOnlySpan<byte> mac, bool isHeader)
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

    /// <summary>
    /// adapted from ethereumJ
    /// </summary>
    private void UpdateMac(KeccakHash mac, KeccakHash macCopy, byte[] seed, Span<byte> output)
    {
        byte[] aesBlock = _egressAesBlockBuffer;
        DoFinalNoReset(mac, macCopy, aesBlock);

        _aesEngine.ProcessBlock(aesBlock, 0, aesBlock, 0);

        // Note that although the mac digest size is 32 bytes, we only use 16 bytes in the computation
        int length = 16;
        for (int i = 0; i < length; i++)
        {
            aesBlock[i] ^= seed[i];
        }

        mac.Update(aesBlock.AsSpan(0, length));
        byte[] result = seed;
        DoFinalNoReset(mac, macCopy, result);

        result.AsSpan(0, length).CopyTo(output);
    }

    private static void DoFinalNoReset(KeccakHash mac, KeccakHash macCopy, byte[] output)
    {
        macCopy.ResetTo(mac);
        macCopy.UpdateFinalTo(output);
    }

    // can still be used after Dispose - copies are returned to the pool, but lazily restored if needed
    public void Dispose()
    {
        _egressMacCopy.Reset();
        _ingressMacCopy.Reset();
    }
}
