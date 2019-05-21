/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.IO;
using System.Security.Cryptography;
using Nethermind.KeyStore.Config;
using Nethermind.Logging;

namespace Nethermind.KeyStore
{
    public class AesEncrypter : ISymmetricEncrypter
    {
        private readonly IKeyStoreConfig _config;
        private readonly ILogger _logger;

        public AesEncrypter(IKeyStoreConfig keyStoreConfig, ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _config = keyStoreConfig;
        }

        public byte[] Encrypt(byte[] content, byte[] key, byte[] iv, string cipherType)
        {
            try
            {
                using (var aes = CreateAesCryptoServiceProvider(key, iv, cipherType))
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

        public byte[] Decrypt(byte[] cipher, byte[] key, byte[] iv, string cipherType)
        {
            try
            {
                using (var aes = CreateAesCryptoServiceProvider(key, iv, cipherType))
                {
                    var decryptor = aes.CreateDecryptor(key, aes.IV);
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

        private SymmetricAlgorithm CreateAesCryptoServiceProvider(byte[] key, byte[] iv, string cipherType)
        {
            switch (cipherType)
            {
                case "aes-128-ctr":
                    //Custom impl for AES128 CTR
                    return new Aes128CounterMode(iv);
                case "aes-128-cbc":
                    return new AesCryptoServiceProvider
                    {
                        BlockSize = _config.SymmetricEncrypterBlockSize,
                        KeySize = _config.SymmetricEncrypterKeySize,
                        Padding = PaddingMode.PKCS7,
                        Key = key,
                        IV = iv
                    };
                default:
                    throw new Exception($"Unsupported cipherType: {cipherType}");
            }
        }
    }
}