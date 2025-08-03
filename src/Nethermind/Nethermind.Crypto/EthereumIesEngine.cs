// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

using Nethermind.Core.Extensions;

using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Utilities;

namespace Nethermind.Crypto;

/// <summary>
///     Code adapted from ethereumJ (https://github.com/ethereum/ethereumj)
///     Support class for constructing integrated encryption cipher
///     for doing basic message exchanges on top of key agreement ciphers.
///     Follows the description given in IEEE Std 1363a with a couple of changes
///     specific to Ethereum:
///     -Hash the MAC key before use
///     -Include the encryption IV in the MAC computation
/// </summary>
public sealed class EthereumIesEngine
{
    private bool _forEncryption;
    private byte[] _kdfKey;
    private readonly IDigest _hash;
    private readonly IMac _mac;
    private readonly BufferedBlockCipher _cipher;
    private IesWithCipherParameters _iesParameters;
    private byte[] _iv;

    /**
     * set up for use with stream mode, where the key derivation function
     * is used to provide a stream of bytes to xor with the message.
     *  @param agree the key agreement used as the basis for the encryption
     * @param kdf    the key derivation function used for byte generation
     * @param mac    the message authentication code generator for the message
     * @param hash   hash ing function
     * @param cipher the actual cipher
     */
    public EthereumIesEngine(IMac mac, IDigest hash, BufferedBlockCipher cipher)
    {
        _mac = mac;
        _hash = hash;
        _cipher = cipher;
    }

    /**
     * Initialise the encryptor.
     *
     * @param forEncryption whether or not this is encryption/decryption.
     * @param privParam     our private key parameters
     * @param pubParam      the recipient's/sender's public key parameters
     * @param params        encoding and derivation parameters, may be wrapped to include an IV for an underlying block cipher.
     */
    public void Init(bool forEncryption, byte[] kdfKey, IesWithCipherParameters parameters, byte[] iv)
    {
        _kdfKey = kdfKey;
        _forEncryption = forEncryption;
        _iv = iv;
        _iesParameters = parameters;
    }

    private byte[] EncryptBlock(byte[] input, byte[] macData)
    {
        // Block cipher mode.
        ReadOnlySpan<byte> k1 = _kdfKey.AsSpan(0, _iesParameters.CipherKeySize / 8);
        _cipher.Init(true, new ParametersWithIV(new KeyParameter(k1), _iv));

        Span<byte> c = new byte[_cipher.GetOutputSize(input.Length)];
        int len = _cipher.ProcessBytes(input, c);
        len += _cipher.DoFinal(c.Slice(len));

        _hash.Reset();
        ReadOnlySpan<byte> k2 = _kdfKey.AsSpan(k1.Length, _iesParameters.MacKeySize / 8);
        _hash.BlockUpdate(k2);

        Span<byte> k2A = new byte[_hash.GetDigestSize()];
        _hash.DoFinal(k2A);

        _mac.Init(new KeyParameter(k2A));
        _mac.BlockUpdate(_iv, 0, _iv.Length);
        _mac.BlockUpdate(c);

        // Convert the length of the encoding vector into a byte array.
        byte[]? p2 = _iesParameters.GetEncodingV();
        if (p2 is not null)
        {
            _mac.BlockUpdate(p2, 0, p2.Length);
        }

        if (macData is not null)
        {
            _mac.BlockUpdate(macData, 0, macData.Length);
        }

        // Apply the MAC.
        Span<byte>  T = new byte[_mac.GetMacSize()];
        _mac.DoFinal(T);

        // Output the double (C,T).
        byte[] output = new byte[len + T.Length];
        c.Slice(0, len).CopyTo(output.AsSpan(0, len));
        T.CopyTo(output.AsSpan(len, T.Length));
        return output;
    }

    private byte[] DecryptBlock(byte[] input, byte[]? macData)
    {
        // Ensure that the length of the input is greater than the MAC in bytes
        if (input.Length <= _iesParameters.MacKeySize / 8)
        {
            throw new InvalidCipherTextException("Length of input must be greater than the MAC");
        }

        // Block cipher mode.
        ReadOnlySpan<byte> k1 = _kdfKey.AsSpan(0, _iesParameters.CipherKeySize / 8);
        _cipher.Init(false, new ParametersWithIV(new KeyParameter(k1), _iv));

        byte[] M = new byte[_cipher.GetOutputSize(input.Length - _mac.GetMacSize())];
        int len = _cipher.ProcessBytes(input.Slice(0, input.Length - _mac.GetMacSize()), M, 0);
        len += _cipher.DoFinal(M, len);

        // Verify the MAC.
        int end = input.Length;
        int macSize = _mac.GetMacSize();
        ReadOnlySpan<byte> t1 = input.AsSpan(end - macSize, macSize);

        byte[] k2A = new byte[_hash.GetDigestSize()];
        _hash.Reset();
        ReadOnlySpan<byte> k2 = _kdfKey.AsSpan(k1.Length, _iesParameters.MacKeySize / 8);
        _hash.BlockUpdate(k2);
        _hash.DoFinal(k2A, 0);

        _mac.Init(new KeyParameter(k2A));
        _mac.BlockUpdate(_iv, 0, _iv.Length);
        _mac.BlockUpdate(input.Slice(0, input.Length - t1.Length));

        // Convert the length of the encoding vector into a byte array.
        byte[]? p2 = _iesParameters.GetEncodingV();
        if (p2 is not null)
        {
            _mac.BlockUpdate(p2, 0, p2.Length);
        }

        if (macData is not null)
        {
            _mac.BlockUpdate(macData, 0, macData.Length);
        }

        byte[] t2 = new byte[t1.Length];
        _mac.DoFinal(t2);

        if (!Arrays.FixedTimeEquals(t1, t2))
        {
            throw new InvalidCipherTextException("Invalid MAC.");
        }

        // Output the message.
        return Arrays.CopyOfRange(M, 0, len);
    }

    public byte[] ProcessBlock(byte[] input, byte[] macData)
    {
        return _forEncryption
            ? EncryptBlock(input, macData)
            : DecryptBlock(input, macData);
    }
}
