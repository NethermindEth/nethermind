//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
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
            _config = keyStoreConfig ?? throw new ArgumentNullException(nameof(keyStoreConfig));
            _logger = logManager?.GetClassLogger<AesEncrypter>() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public byte[] Encrypt(byte[] content, byte[] key, byte[] iv, string cipherType)
        {
            try
            {
                switch (cipherType)
                {
                    case "aes-128-cbc":
                    {
                        using var aes = new AesCryptoServiceProvider
                        {
                            BlockSize = _config.SymmetricEncrypterBlockSize,
                            KeySize = _config.SymmetricEncrypterKeySize,
                            Padding = PaddingMode.PKCS7,
                            Key = key,
                            IV = iv
                        };
                        var encryptor = aes.CreateEncryptor(aes.Key, aes.IV);
                        return Execute(encryptor, content);
                    }
                    case "aes-128-ctr":
                    {
                        using var outputEncryptedStream = new MemoryStream();
                        using var inputStream = new MemoryStream(content);
                        AesCtr(key, iv, inputStream, outputEncryptedStream);
                        outputEncryptedStream.Position = 0;
                        return outputEncryptedStream.ToArray();
                    }
                    default:
                        throw new Exception($"Unsupported cipherType: {cipherType}");
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
                switch (cipherType)
                {
                    case "aes-128-cbc":
                    {
                        using var aes = new AesCryptoServiceProvider
                        {
                            BlockSize = _config.SymmetricEncrypterBlockSize,
                            KeySize = _config.SymmetricEncrypterKeySize,
                            Padding = PaddingMode.PKCS7,
                            Key = key,
                            IV = iv
                        };
                        var decryptor = aes.CreateDecryptor(key, aes.IV);
                        return Execute(decryptor, cipher);
                    }
                    case "aes-128-ctr":
                    {
                        using var outputEncryptedStream = new MemoryStream(cipher);
                        using var outputDecryptedStream = new MemoryStream();
                        AesCtr(key, iv, outputEncryptedStream, outputDecryptedStream);
                        outputDecryptedStream.Position = 0;
                        return outputDecryptedStream.ToArray();
                    }
                    default:
                        throw new Exception($"Unsupported cipherType: {cipherType}");
                }
            }
            catch (Exception ex)
            {
                _logger.Error("Error during encryption", ex);
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

        private static void AesCtr(byte[] key, byte[] salt, Stream inputStream, Stream outputStream)
        {
            using var aes = new AesManaged {Mode = CipherMode.ECB, Padding = PaddingMode.None};
            var blockSize = aes.BlockSize / 8;
            if (salt.Length != blockSize)
            {
                throw new ArgumentException($"Salt size must be same as block size ({salt.Length} != {blockSize})");
            }

            var counter = (byte[]) salt.Clone();
            var xorMask = new Queue<byte>();
            var zeroIv = new byte[blockSize];
            var encryptor = aes.CreateEncryptor(key, zeroIv);

            int @byte;
            while ((@byte = inputStream.ReadByte()) != -1)
            {
                if (xorMask.Count == 0)
                {
                    var counterModeBlock = new byte[blockSize];
                    encryptor.TransformBlock(counter, 0, counter.Length, counterModeBlock, 0);

                    for (var i = counter.Length - 1; i >= 0; i--)
                    {
                        if (++counter[i] != 0)
                        {
                            break;
                        }
                    }

                    foreach (var block in counterModeBlock)
                    {
                        xorMask.Enqueue(block);
                    }
                }

                var mask = xorMask.Dequeue();
                outputStream.WriteByte((byte) ((byte) @byte ^ mask));
            }
        }
    }
}
