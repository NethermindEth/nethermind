using System;
using System.IO;
using System.Security.Cryptography;
using Nevermind.Core;

namespace Nevermind.KeyStore
{
    public class AesEncrypter : ISymmetricEncrypter
    {
        private readonly IConfigurationProvider _configurationProvider;
        private readonly ILogger _logger;

        public AesEncrypter(IConfigurationProvider configurationProvider, ILogger logger)
        {
            _configurationProvider = configurationProvider;
            _logger = logger;
        }

        public byte[] Encrypt(byte[] content, byte[] key, byte[] iv)
        {
            try
            {
                using (var aes = CreateAesCryptoServiceProvider(key, iv))
                {
                    var encryptor = aes.CreateEncryptor(aes.Key, aes.IV);
                    return Execute(encryptor, content);
                }
            }
            catch (Exception ex)
            {
                _logger.Error("Error during encryption", ex);
                return null;
            }
        }

        public byte[] Decrypt(byte[] cipher, byte[] key, byte[] iv)
        {
            try
            {
                using (var aes = CreateAesCryptoServiceProvider(key, iv))
                {
                    var decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
                    return Execute(decryptor, cipher);
                }
            }
            catch (Exception ex)
            {
                _logger.Error("Error during decryption", ex);
                return null;
            }
        }

        private byte[] Execute(ICryptoTransform cryptoTransform, byte[] data)
        {
            using (var memoryStream = new MemoryStream())
            {
                using (var cryptoStream = new CryptoStream(memoryStream, cryptoTransform, CryptoStreamMode.Write))
                {
                    cryptoStream.Write(data, 0, data.Length);
                    cryptoStream.FlushFinalBlock();
                    return memoryStream.ToArray();
                }
            }
        }

        private AesCryptoServiceProvider CreateAesCryptoServiceProvider(byte[] key, byte[] iv)
        {
            var aes = new AesCryptoServiceProvider
            {
                BlockSize = _configurationProvider.SymmetricEncrypterBlockSize,
                KeySize = _configurationProvider.SymmetricEncrypterKeySize,
                Padding = PaddingMode.PKCS7,
                Key = key,
                IV = iv
            };
            return aes;
        }
    }
}