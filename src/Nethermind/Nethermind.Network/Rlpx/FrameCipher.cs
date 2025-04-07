// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Security.Cryptography;
using Nethermind.Crypto;

namespace Nethermind.Network.Rlpx;

public class FrameCipher : IFrameCipher
{
    private const int KeySize = 32;

    private readonly ICryptoTransform _decryptionCipher;
    private readonly ICryptoTransform _encryptionCipher;

    public FrameCipher(byte[] aesKey)
    {
        Debug.Assert(aesKey.Length == KeySize, $"AES key expected to be {KeySize} bytes long");

        var aes = new AesCtr(aesKey);

        _encryptionCipher = aes.CreateEncryptor();
        _decryptionCipher = aes.CreateDecryptor();
    }

    public void Encrypt(byte[] input, int offset, int length, byte[] output, int outputOffset)
    {
        _encryptionCipher.TransformBlock(input, offset, length, output, outputOffset);
    }

    public void Decrypt(byte[] input, int offset, int length, byte[] output, int outputOffset)
    {
        _decryptionCipher.TransformBlock(input, offset, length, output, outputOffset);
    }
}
