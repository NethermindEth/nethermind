// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace Nethermind.Network.Rlpx;

public class FrameCipher : IFrameCipher
{
    private const int BlockSize = 16;
    private const int KeySize = 32;

    private readonly IBufferedCipher _decryptionCipher;
    private readonly IBufferedCipher _encryptionCipher;

    public FrameCipher(byte[] aesKey)
    {
        IBlockCipher aes = AesUtilities.CreateEngine();

        Debug.Assert(aesKey.Length == KeySize, $"AES key expected to be {KeySize} bytes long");

        _encryptionCipher = new BufferedBlockCipher(new SicBlockCipher(aes));
        _encryptionCipher.Init(true, new ParametersWithIV(ParameterUtilities.CreateKeyParameter("AES", aesKey), new byte[BlockSize]));

        _decryptionCipher = new BufferedBlockCipher(new SicBlockCipher(aes));
        _decryptionCipher.Init(false, new ParametersWithIV(ParameterUtilities.CreateKeyParameter("AES", aesKey), new byte[BlockSize]));
    }

    public void Encrypt(ReadOnlySpan<byte> input, Span<byte> output) => _encryptionCipher.ProcessBytes(input, output);

    public void Decrypt(ReadOnlySpan<byte> input, Span<byte> output) => _decryptionCipher.ProcessBytes(input, output);
}
