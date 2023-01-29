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
    private byte[] _nonce;
    private readonly byte[] _zeroIV;

    /// <summary>
    /// Initializes a new instance of the AesCtr class
    /// with a provided key and initialization vector (IV/nonce) if any.
    /// </summary>
    /// <param name="key">The secret key to use for this instance.</param>
    /// <param name="iv">The IV/nonce to use for this instance.</param>
    public AesCtr(byte[]? key = null, byte[]? iv = null)
    {
        _aes = Aes.Create();
        _aes.Mode = CipherMode.ECB;
        _aes.Padding = PaddingMode.None;

        if (key is not null)
            _aes.Key = key;

        _zeroIV = new byte[_aes.BlockSize / 8];
        _aes.IV = _zeroIV;
        IV = iv ?? _zeroIV;
    }

    ///// <summary>
    ///// Initializes a new instance of the AesCtr class with a provided <see cref="Aes"/> instance.
    ///// </summary>
    ///// <param name="aes"></param>
    ///// <exception cref="ArgumentNullException">
    ///// The <code>aes</code> parameter is <code>null</code>.
    ///// </exception>
    ///// <exception cref="CryptographicException">The cipher mode or padding is invalid.</exception>
    //public AesCtr(Aes aes)
    //{
    //    _aes = aes ?? throw new ArgumentNullException(nameof(aes));

    //    if (_aes.Mode != CipherMode.ECB)
    //        throw new CryptographicException("Invalid cipher mode.");

    //    if (_aes.Padding != PaddingMode.None)
    //        throw new CryptographicException("Invalid padding mode.");

    //    _zeroIV = new byte[_aes.BlockSize / 8];
    //}

    public override ICryptoTransform CreateDecryptor(byte[] rgbKey, byte[]? rgbIV = null)
        => CreateEncryptor(rgbKey, rgbIV);

    public override ICryptoTransform CreateEncryptor(byte[] rgbKey, byte[]? rgbIV = null)
        => new AesCtrTransform(_aes.CreateEncryptor(rgbKey, _zeroIV), rgbIV ?? _nonce);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _aes.Dispose();
    }

    public override void GenerateIV() => RandomNumberGenerator.Fill(_nonce);

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

    public override byte[] IV
    {
        get => _nonce;
        set
        {
            ArgumentNullException.ThrowIfNull(value);

            if (value.Length % (BlockSize / 8) != 0)
                throw new CryptographicException("Specified initialization vector (IV) does not match the block size for this algorithm.");

            _nonce = value;
        }
    }

    public override byte[] Key { get => _aes.Key; set => _aes.Key = value; }

    public override int KeySize { get => _aes.KeySize; set => _aes.KeySize = value; }

    public override KeySizes[] LegalBlockSizes => _aes.LegalBlockSizes;

    public override KeySizes[] LegalKeySizes => _aes.LegalKeySizes;

    /// <returns><see cref="CipherMode.ECB"/></returns>
    /// <remarks>Supports <see cref="CipherMode.ECB"/> only.</remarks>
    /// <inheritdoc/>
    public override CipherMode Mode
    {
        get => _aes.Mode;
        set
        {
            if (value != CipherMode.ECB)
                throw new CryptographicException("Invalid cipher mode.");

            _aes.Mode = value;
        }
    }

    /// <returns><see cref="PaddingMode.None"/></returns>
    /// <remarks>Supports <see cref="PaddingMode.None"/> only.</remarks>
    /// <inheritdoc/>
    public override PaddingMode Padding
    {
        get => _aes.Padding;
        set
        {
            if (value != PaddingMode.None)
                throw new CryptographicException("Invalid padding mode.");

            _aes.Padding = value;
        }
    }
}
