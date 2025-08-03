// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Macs;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Utilities;

namespace Nethermind.Crypto;

/// <summary>
/// Code adapted from ethereumJ (https://github.com/ethereum/ethereumj)
/// Support class for constructing integrated encryption cipher
/// for doing basic message exchanges on top of key agreement ciphers.
/// 
/// Follows the description given in IEEE Std 1363a with a couple of changes
/// specific to Ethereum:
///  * Hash the MAC key before use
///  * Include the encryption IV in the MAC computation
/// </summary>
public sealed class EthereumIesEngine
{
    private bool _forEncryption;
    private byte[] _kdfKey;
    private readonly Sha256Digest _hash;
    private readonly HMac _mac;
    private readonly BufferedBlockCipher _cipher;
    private IesWithCipherParameters _iesParameters;
    private byte[] _iv;

    /// <summary>
    /// Initializes a new instance of the <see cref="EthereumIesEngine"/> class.
    /// </summary>
    /// <param name="mac">The message authentication code generator for the message.</param>
    /// <param name="hash">The hash function.</param>
    /// <param name="cipher">The block cipher to use for encryption/decryption.</param>
    public EthereumIesEngine(HMac mac, Sha256Digest hash, BufferedBlockCipher cipher)
    {
        _mac = mac;
        _hash = hash;
        _cipher = cipher;
    }

    /// <summary>
    /// Initializes the engine for encryption or decryption.
    /// </summary>
    /// <param name="forEncryption">True to initialize for encryption, false for decryption.</param>
    /// <param name="kdfKey">The key derived from the key agreement protocol.</param>
    /// <param name="parameters">The IES parameters, including cipher and MAC key sizes and optional encoding vector.</param>
    /// <param name="iv">The initialization vector for the underlying block cipher.</param>
    public void Init(bool forEncryption, byte[] kdfKey, IesWithCipherParameters parameters, byte[] iv)
    {
        _kdfKey = kdfKey;
        _forEncryption = forEncryption;
        _iv = iv;
        _iesParameters = parameters;
    }

    /// <summary>
    /// Processes a block of input data, performing encryption or decryption and authentication.
    /// </summary>
    /// <param name="input">The input data to encrypt or decrypt.</param>
    /// <param name="macData">Additional data to include in the MAC computation (can be null or empty).</param>
    /// <returns>The resulting encrypted or decrypted data, with authentication applied.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="input"/> is null.</exception>
    public byte[] ProcessBlock(byte[] input, byte[] macData)
    {
        ArgumentNullException.ThrowIfNull(input);

        int outputSize = GetOutputSize(input.Length);
        byte[] output = new byte[outputSize];
        int actualLength = _forEncryption
            ? EncryptBlock(input, output, macData)
            : DecryptBlock(input, output, macData);

        if (actualLength == outputSize)
            return output;

        byte[] result = new byte[actualLength];
        output.AsSpan(0, actualLength).CopyTo(result);
        return result;
    }

    private int GetOutputSize(int inputLength)
    {
        int macSize = _mac.GetMacSize();
        return _cipher.GetOutputSize(inputLength) +
            (_forEncryption ? macSize : -macSize);
    }

    private int EncryptBlock(ReadOnlySpan<byte> input, Span<byte> output, ReadOnlySpan<byte> macData)
    {
        // Block cipher mode.
        ReadOnlySpan<byte> k1 = _kdfKey.AsSpan(0, _iesParameters.CipherKeySize / 8);
        _cipher.Init(true, new ParametersWithIV(new KeyParameter(k1), _iv));

        int cipherOutputSize = _cipher.GetOutputSize(input.Length);
        int macSize = _mac.GetMacSize();
        int digestSize = _hash.GetDigestSize();

        if (output.Length < cipherOutputSize + macSize)
        {
            ThrowOutputBufferTooSmall();
        }

        // Rent temporary buffers from pool
        byte[] cipherOutputBuffer = ArrayPool<byte>.Shared.Rent(cipherOutputSize);
        byte[] k2ABuffer = ArrayPool<byte>.Shared.Rent(digestSize);
        byte[] macOutputBuffer = ArrayPool<byte>.Shared.Rent(macSize);

        Span<byte> c = cipherOutputBuffer.AsSpan(0, cipherOutputSize);
        int len = _cipher.ProcessBytes(input, c);
        len += _cipher.DoFinal(c.Slice(len));

        _hash.Reset();
        ReadOnlySpan<byte> k2 = _kdfKey.AsSpan(k1.Length, _iesParameters.MacKeySize / 8);
        _hash.BlockUpdate(k2);

        Span<byte> k2A = k2ABuffer.AsSpan(0, digestSize);
        _hash.DoFinal(k2A);

        _mac.Init(new KeyParameter(k2A));
        _mac.BlockUpdate(_iv);
        _mac.BlockUpdate(c.Slice(0, len));

        // Convert the length of the encoding vector into a byte array.
        byte[]? p2 = _iesParameters.GetEncodingV();
        if (p2 is not null)
        {
            _mac.BlockUpdate(p2, 0, p2.Length);
        }

        if (!macData.IsEmpty)
        {
            _mac.BlockUpdate(macData);
        }

        // Apply the MAC.
        Span<byte> T = macOutputBuffer.AsSpan(0, macSize);
        _mac.DoFinal(T);

        // Output the double (C,T).
        c.Slice(0, len).CopyTo(output);
        T.CopyTo(output.Slice(len));

        // Return buffers to the pool
        ArrayPool<byte>.Shared.Return(cipherOutputBuffer);
        ArrayPool<byte>.Shared.Return(k2ABuffer);
        ArrayPool<byte>.Shared.Return(macOutputBuffer);

        return len + macSize;

        [StackTraceHidden, DoesNotReturn]
        static void ThrowOutputBufferTooSmall()
            => throw new ArgumentException("Output buffer too small", nameof(output));
    }

    private int DecryptBlock(ReadOnlySpan<byte> input, Span<byte> output, ReadOnlySpan<byte> macData)
    {
        int macSize = _mac.GetMacSize();

        // Ensure that the length of the input is greater than the MAC in bytes
        if (input.Length <= _iesParameters.MacKeySize / 8)
        {
            ThrowInputTooShort();
        }

        int digestSize = _hash.GetDigestSize();

        // Rent temporary buffers from pool
        byte[] k2ABuffer = ArrayPool<byte>.Shared.Rent(digestSize);
        byte[] macOutputBuffer = ArrayPool<byte>.Shared.Rent(macSize);

        // Block cipher mode.
        ReadOnlySpan<byte> k1 = _kdfKey.AsSpan(0, _iesParameters.CipherKeySize / 8);
        _cipher.Init(false, new ParametersWithIV(new KeyParameter(k1), _iv));

        int cipherInputLength = input.Length - macSize;

        // Verify the MAC.
        ReadOnlySpan<byte> t1 = input.Slice(input.Length - macSize, macSize);

        _hash.Reset();
        ReadOnlySpan<byte> k2 = _kdfKey.AsSpan(k1.Length, _iesParameters.MacKeySize / 8);
        _hash.BlockUpdate(k2);

        Span<byte> k2A = k2ABuffer.AsSpan(0, digestSize);
        _hash.DoFinal(k2A);

        _mac.Init(new KeyParameter(k2A));
        _mac.BlockUpdate(_iv);
        _mac.BlockUpdate(input.Slice(0, cipherInputLength));

        // Convert the length of the encoding vector into a byte array.
        byte[]? p2 = _iesParameters.GetEncodingV();
        if (p2 is not null)
        {
            _mac.BlockUpdate(p2, 0, p2.Length);
        }

        if (!macData.IsEmpty)
        {
            _mac.BlockUpdate(macData);
        }

        Span<byte> t2 = macOutputBuffer.AsSpan(0, macSize);
        _mac.DoFinal(t2);

        if (!Arrays.FixedTimeEquals(t1, t2))
        {
            ThrowInvalidMac();
        }

        // Decrypt the message
        int len = _cipher.ProcessBytes(input.Slice(0, cipherInputLength), output);
        len += _cipher.DoFinal(output.Slice(len));

        // Return buffers to the pool
        ArrayPool<byte>.Shared.Return(k2ABuffer);
        ArrayPool<byte>.Shared.Return(macOutputBuffer);

        return len;

        [StackTraceHidden, DoesNotReturn]
        static void ThrowInputTooShort()
            => throw new InvalidCipherTextException("Length of input must be greater than the MAC");

        [StackTraceHidden, DoesNotReturn]
        static void ThrowInvalidMac()
            => throw new InvalidCipherTextException("Invalid MAC.");
    }
}
