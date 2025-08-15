extern alias BouncyCastleCryptography;
using BouncyCastleCryptography::Org.BouncyCastle.Crypto;
using BouncyCastleCryptography::Org.BouncyCastle.Crypto.Parameters;
using BouncyCastleCryptography::Org.BouncyCastle.Security;
using BouncyCastleCryptography::Org.BouncyCastle.Crypto.Modes;
using BouncyCastleCryptography::Org.BouncyCastle.Crypto.Engines;

namespace Lantern.Discv5.WireProtocol.Session;

/// <summary>
/// Provides utility methods for handling AES cryptography operations.
/// </summary>
public class AesCrypto : IAesCrypto
{
    private const int AesBlockSize = 16;
    private const int GcmTagSize = 128;

    public byte[] AesCtrEncrypt(byte[] maskingKey, byte[] maskingIv, byte[] header)
    {
        if (maskingKey.Length != AesBlockSize || maskingIv.Length != AesBlockSize)
        {
            throw new ArgumentException($"Invalid {nameof(maskingKey)} or {nameof(maskingIv)} length.");
        }

        var cipher = CipherUtilities.GetCipher("AES/CTR/NoPadding");
        var parameters = CreateAesCtrCipherParameters(maskingKey, maskingIv);

        cipher.Init(true, parameters);

        var cipherText = new byte[cipher.GetOutputSize(header.Length)];
        var len = cipher.ProcessBytes(header, 0, header.Length, cipherText, 0);
        cipher.DoFinal(cipherText, len);

        return cipherText;
    }

    public byte[]? AesCtrDecrypt(byte[] maskingKey, byte[] maskingIv, byte[] maskedHeader)
    {
        if (maskingKey.Length != AesBlockSize || maskingIv.Length != AesBlockSize)
        {
            throw new ArgumentException($"Invalid {nameof(maskingKey)} or {nameof(maskingIv)} length.");
        }

        var cipher = CipherUtilities.GetCipher("AES/CTR/NoPadding");
        var parameters = CreateAesCtrCipherParameters(maskingKey, maskingIv);

        cipher.Init(false, parameters);
        byte[]? result;

        try
        {
            result = cipher.DoFinal(maskedHeader);
        }
        catch (Exception)
        {
            return null;
        }

        return result;
    }

    public byte[] AesGcmEncrypt(byte[] key, byte[] nonce, byte[] plaintext, byte[] ad)
    {
        var cipher = new GcmBlockCipher(new AesEngine());
        var parameters = new AeadParameters(new KeyParameter(key), GcmTagSize, nonce, ad);

        cipher.Init(true, parameters);

        var ciphertext = new byte[cipher.GetOutputSize(plaintext.Length)];
        var len = cipher.ProcessBytes(plaintext, 0, plaintext.Length, ciphertext, 0);

        cipher.DoFinal(ciphertext, len);

        return ciphertext;
    }

    public byte[]? AesGcmDecrypt(byte[] key, byte[] nonce, byte[] ciphertext, byte[] ad)
    {
        var cipher = new GcmBlockCipher(new AesEngine());
        var parameters = new AeadParameters(new KeyParameter(key), GcmTagSize, nonce, ad);

        cipher.Init(false, parameters);

        var plaintext = new byte[cipher.GetOutputSize(ciphertext.Length)];
        var len = cipher.ProcessBytes(ciphertext, 0, ciphertext.Length, plaintext, 0);

        try
        {
            cipher.DoFinal(plaintext, len);
        }
        catch (Exception)
        {
            return null;
        }

        return plaintext;
    }

    private static ICipherParameters CreateAesCtrCipherParameters(byte[] maskingKey, byte[] maskingIv)
    {
        var keyParam = new KeyParameter(maskingKey);
        return new ParametersWithIV(keyParam, maskingIv);
    }
}