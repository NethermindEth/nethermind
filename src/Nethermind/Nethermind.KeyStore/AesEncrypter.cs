// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Microsoft.IO;
using Nethermind.Core.Resettables;
using Nethermind.KeyStore.Config;
using Nethermind.Logging;

namespace Nethermind.KeyStore
{
    public class AesEncrypter(IKeyStoreConfig keyStoreConfig, ILogManager logManager) : ISymmetricEncrypter
    {
        private readonly IKeyStoreConfig _config = keyStoreConfig ?? throw new ArgumentNullException(nameof(keyStoreConfig));
        private readonly ILogger _logger = logManager?.GetClassLogger<AesEncrypter>() ?? throw new ArgumentNullException(nameof(logManager));

        public byte[] Encrypt(byte[] content, byte[] key, byte[] iv, string cipherType)
        {
            try
            {
                switch (cipherType)
                {
                    case "aes-128-cbc":
                        {
                            using Aes aes = Aes.Create();
                            aes.BlockSize = _config.SymmetricEncrypterBlockSize;
                            aes.KeySize = _config.SymmetricEncrypterKeySize;
                            aes.Padding = PaddingMode.PKCS7;
                            aes.Key = key;
                            aes.IV = iv;

                            using ICryptoTransform encryptor = aes.CreateEncryptor(aes.Key, aes.IV);
                            return Execute(encryptor, content);
                        }
                    case "aes-128-ctr":
                        {
                            using RecyclableMemoryStream outputEncryptedStream = RecyclableStream.GetStream("aes-128-ctr-encrypt");
                            using MemoryStream inputStream = new(content);
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
                            using Aes aes = Aes.Create();
                            aes.BlockSize = _config.SymmetricEncrypterBlockSize;
                            aes.KeySize = _config.SymmetricEncrypterKeySize;
                            aes.Padding = PaddingMode.PKCS7;
                            aes.Key = key;
                            aes.IV = iv;
                            using ICryptoTransform decryptor = aes.CreateDecryptor(key, aes.IV);
                            return Execute(decryptor, cipher);
                        }
                    case "aes-128-ctr":
                        {
                            using MemoryStream outputEncryptedStream = new(cipher);
                            using RecyclableMemoryStream outputDecryptedStream = RecyclableStream.GetStream("aes-128-ctr-decrypt");
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

        private static byte[] Execute(ICryptoTransform cryptoTransform, byte[] data)
        {
            using RecyclableMemoryStream memoryStream = RecyclableStream.GetStream(nameof(AesEncrypter));
            using CryptoStream cryptoStream = new(memoryStream, cryptoTransform, CryptoStreamMode.Write, leaveOpen: true);
            cryptoStream.Write(data, 0, data.Length);
            cryptoStream.FlushFinalBlock();
            return memoryStream.ToArray();
        }

        private static void AesCtr(byte[] key, byte[] salt, Stream inputStream, Stream outputStream)
        {
            using Aes aes = Aes.Create();
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            int blockSize = aes.BlockSize / 8;
            if (salt.Length != blockSize)
            {
                throw new ArgumentException($"Salt size must be same as block size ({salt.Length} != {blockSize})");
            }

            byte[] counter = (byte[])salt.Clone();
            Queue<byte> xorMask = new();
            byte[] zeroIv = new byte[blockSize];
            using ICryptoTransform encryptor = aes.CreateEncryptor(key, zeroIv);

            int @byte;
            while ((@byte = inputStream.ReadByte()) != -1)
            {
                if (xorMask.Count == 0)
                {
                    byte[] counterModeBlock = new byte[blockSize];
                    encryptor.TransformBlock(counter, 0, counter.Length, counterModeBlock, 0);

                    for (int i = counter.Length - 1; i >= 0; i--)
                    {
                        if (++counter[i] != 0)
                        {
                            break;
                        }
                    }

                    foreach (byte block in counterModeBlock)
                    {
                        xorMask.Enqueue(block);
                    }
                }

                byte mask = xorMask.Dequeue();
                outputStream.WriteByte((byte)((byte)@byte ^ mask));
            }
        }
    }
}
