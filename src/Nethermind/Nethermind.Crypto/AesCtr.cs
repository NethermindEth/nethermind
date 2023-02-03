// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Security.Cryptography;

namespace Nethermind.Crypto;

/// <summary>
/// Represents an Advanced Encryption Standard (AES) key
/// to be used with the Counter (CTR) mode of operation.
/// </summary>
public class AesCtr : SymmetricAlgorithm
{
    private readonly Aes _aes;

    /// <summary>
    /// Initializes a new instance of the AesCtr class
    /// with a provided key and initialization vector (IV) if any.
    /// </summary>
    /// <param name="key">The secret key to use for this instance.</param>
    /// <param name="iv">The IV to use for this instance.</param>
    public AesCtr(byte[]? key = null, byte[]? iv = null)
    {
        _aes = Aes.Create();
        _aes.IV = iv ?? new byte[_aes.IV.Length];
        _aes.Mode = CipherMode.ECB;
        _aes.Padding = PaddingMode.None;

        if (key is not null)
            _aes.Key = key;
    }

    public override ICryptoTransform CreateDecryptor(byte[] rgbKey, byte[]? rgbIV = null)
        => CreateEncryptor(rgbKey, rgbIV);

    public override ICryptoTransform CreateEncryptor(byte[] rgbKey, byte[]? rgbIV = null)
        => new AesCtrTransform(_aes.CreateEncryptor(rgbKey, rgbIV), rgbIV ?? _aes.IV);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _aes.Dispose();
    }

    public override void GenerateIV() => _aes.GenerateIV();

    public override void GenerateKey() => _aes.GenerateKey();

    public override int BlockSize { get => _aes.BlockSize; set => _aes.BlockSize = value; }

    /// <summary>
    /// The feedback size is not supported for the CTR cipher mode.
    /// </summary>
    /// <exception cref="NotSupportedException"/>
    public override int FeedbackSize
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override byte[] IV { get => _aes.IV; set => _aes.IV = value; }

    public override byte[] Key { get => _aes.Key; set => _aes.Key = value; }

    public override int KeySize { get => _aes.KeySize; set => _aes.KeySize = value; }

    public override KeySizes[] LegalBlockSizes => _aes.LegalBlockSizes;

    public override KeySizes[] LegalKeySizes => _aes.LegalKeySizes;

    /// <summary>Gets the mode for operation of the symmetric algorithm.</summary>
    /// <returns><see cref="CipherMode.ECB"/></returns>
    /// <exception cref="NotSupportedException">Setting the cipher mode is not supported.</exception>
    public override CipherMode Mode
    {
        get => _aes.Mode;
        set => throw new NotSupportedException();
    }

    /// <summary>Gets the padding mode used in the symmetric algorithm.</summary>
    /// <returns><see cref="PaddingMode.None"/></returns>
    /// <exception cref="NotSupportedException">Setting the padding mode is not supported.</exception>
    public override PaddingMode Padding
    {
        get => _aes.Padding;
        set => throw new NotSupportedException();
    }
}
