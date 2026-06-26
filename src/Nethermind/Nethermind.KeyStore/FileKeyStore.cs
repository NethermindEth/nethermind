// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using CryptSharp.Utility;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.KeyStore.Config;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

[assembly: InternalsVisibleTo("Nethermind.KeyStore.Test")]
namespace Nethermind.KeyStore
{
    /// <summary>
    ///{
    ///    "Address": "0x812137fc063598c01e46c694dab2cb0c23f5c2a1",
    ///    "Crypto": {
    ///        "Cipher": "aes-128-cbc",
    ///        "CipherText": "0xbe8562b251a5e1ddc8ce9d4b5401e4642bb296490521ba8b251fe491eaa8e343488670711cfe1fd5150a9cbc440bcc9b",
    ///        "CipherParams": {
    ///            "IV": "0x197b93a9ec21ffb7e883dbac56092195"
    ///        },
    ///        "KDF": "scrypt",
    ///        "KDFParams": {
    ///            "DkLen": 32,
    ///            "N": 262144,
    ///            "P": 1,
    ///            "R": 8,
    ///            "Salt": "0x981d779773978b52d2198ade820469703e8347ae55ddb36c3fb210fa1ad7c5ae"
    ///        },
    ///        "MAC": "0xc1b15ba69e4a43e8fab9f46afd329bd85fc98c8929f30c769081622938407c76",
    ///        "Version": 1
    ///    },
    ///    "Id": "c970b4ea-9fc7-493b-9f82-93027aa3a6be",
    ///    "Version": 3
    ///}
    /// </summary>
    [DoNotUseInSecuredContext("Untested, also uses lots of unsafe software key generation techniques")]
    public class FileKeyStore : IKeyStore
    {
        private readonly PrivateKeyGenerator _privateKeyGenerator;
        private readonly IKeyStoreConfig _config;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly ISymmetricEncrypter _symmetricEncrypter;
        private readonly ICryptoRandom _cryptoRandom;
        private readonly ILogger _logger;
        private readonly Encoding _keyStoreEncoding;
        private readonly IKeyStoreIOSettingsProvider _keyStoreIOSettingsProvider;

        public FileKeyStore(
            IKeyStoreConfig keyStoreConfig,
            IJsonSerializer jsonSerializer,
            ISymmetricEncrypter symmetricEncrypter,
            ICryptoRandom cryptoRandom,
            ILogManager logManager,
            IKeyStoreIOSettingsProvider keyStoreIOSettingsProvider)
        {
            _logger = logManager?.GetClassLogger<FileKeyStore>() ?? throw new ArgumentNullException(nameof(logManager));
            _config = keyStoreConfig ?? throw new ArgumentNullException(nameof(keyStoreConfig));
            _jsonSerializer = jsonSerializer ?? throw new ArgumentNullException(nameof(jsonSerializer));
            _symmetricEncrypter = symmetricEncrypter ?? throw new ArgumentNullException(nameof(symmetricEncrypter));
            _cryptoRandom = cryptoRandom ?? throw new ArgumentNullException(nameof(cryptoRandom));
            _keyStoreEncoding = _config.KeyStoreEncoding.Equals("UTF-8", StringComparison.OrdinalIgnoreCase)
                ? new UTF8Encoding(false)
                : Encoding.GetEncoding(_config.KeyStoreEncoding);
            _privateKeyGenerator = new PrivateKeyGenerator(_cryptoRandom);
            _keyStoreIOSettingsProvider = keyStoreIOSettingsProvider ?? throw new ArgumentNullException(nameof(keyStoreIOSettingsProvider));
        }

        public int Version => 3;
        public int CryptoVersion => 1;

        public (KeyStoreItem? KeyData, Result Result) Verify(string keyJson)
        {
            try
            {
                KeyStoreItem? keyData = _jsonSerializer.Deserialize<KeyStoreItem>(keyJson);
                if (keyData is null || !HasRequiredKeyData(keyData))
                {
                    return (null, Result.Fail("Invalid key data format"));
                }

                return (keyData, Result.Success);
            }
            catch (Exception)
            {
                return (null, Result.Fail("Invalid key data format"));
            }
        }

        public (byte[]? Key, Result Result) GetKeyBytes(Address address, SecureString password)
        {
            if (!password.IsReadOnly())
            {
                throw new InvalidOperationException("Cannot work with password that is not readonly");
            }

            string? serializedKey = ReadKey(address);
            if (serializedKey is null)
            {
                return (null, Result.Fail("Cannot find key"));
            }
            KeyStoreItem? keyStoreItem = _jsonSerializer.Deserialize<KeyStoreItem>(serializedKey);
            if (keyStoreItem?.Crypto is null)
            {
                return (null, Result.Fail("Cannot deserialize key"));
            }

            if (!TryValidate(
                keyStoreItem,
                out string? macHex,
                out string? ivHex,
                out string? cipherHex,
                out string? saltHex,
                out KdfParams? kdfParams,
                out string? kdf,
                out string? cipherType,
                out Result validationResult))
            {
                return (null, validationResult);
            }

            byte[] mac = Bytes.FromHexString(macHex);
            byte[] iv = Bytes.FromHexString(ivHex);
            byte[] cipher = Bytes.FromHexString(cipherHex);
            byte[] salt = Bytes.FromHexString(saltHex);

            byte[] passBytes = password.ToByteArray(_keyStoreEncoding);

            byte[] derivedKey;
            kdf = kdf.Trim();
            switch (kdf)
            {
                case "scrypt":
                    if (kdfParams is not { R: int r, P: int p, N: int n })
                    {
                        return (null, Result.Fail("Incorrect scrypt KDF parameters"));
                    }

                    // ComputeDerivedKey uses too little stack size in case of multithread processing, which may cause stack overflow.
                    // Switch to single thread if "cost" is too high, see Scrypt.ThreadSMixCalls internals
                    derivedKey = SCrypt.ComputeDerivedKey(passBytes, salt, n, r, p, n > 8192 ? 1 : null, kdfParams.DkLen);
                    break;
                case "pbkdf2":
                    if (kdfParams.C is not int c)
                    {
                        return (null, Result.Fail("Incorrect pbkdf2 KDF parameters"));
                    }

                    derivedKey = Rfc2898DeriveBytes.Pbkdf2(passBytes, salt, c, HashAlgorithmName.SHA256, 256);
                    break;
                default:
                    return (null, Result.Fail($"Unsupported algorithm: {kdf}"));
            }

            Span<byte> restoredMac = Keccak.Compute(derivedKey.Slice(kdfParams.DkLen - 16, 16).Concat(cipher).ToArray()).Bytes;
            if (!CryptographicOperations.FixedTimeEquals(mac, restoredMac))
            {
                return (null, Result.Fail("Incorrect MAC"));
            }

            cipherType = cipherType.Trim();
            byte[] decryptKey;
            if (kdf == "scrypt" && cipherType == "aes-128-cbc")
            {
                decryptKey = Keccak.Compute(derivedKey.Slice(0, 16)).Bytes[..16].ToArray();
            }
            else
            {
                decryptKey = derivedKey.Slice(0, 16);
            }

            byte[]? key = _symmetricEncrypter.Decrypt(cipher, decryptKey, iv, cipherType);
            if (key is null)
            {
                return (null, Result.Fail("Error during decryption"));
            }

            // TODO: maybe only allow to sign here so the key never leaves the area?
            return (key, Result.Success);
        }

        public (PrivateKey? PrivateKey, Result Result) GetKey(Address address, SecureString password)
        {
            (byte[]? Key, Result Result) geyKeyResult = GetKeyBytes(address, password);
            if (geyKeyResult.Result.ResultType == ResultType.Failure || geyKeyResult.Key is null)
            {
                return (null, geyKeyResult.Result);
            }
            return (new PrivateKey(geyKeyResult.Key), geyKeyResult.Result);
        }

        public (ProtectedPrivateKey? PrivateKey, Result Result) GetProtectedKey(Address address, SecureString password)
        {
            (PrivateKey? privateKey, Result result) = GetKey(address, password);
            if (result != Result.Success || privateKey is null)
            {
                return (null, result);
            }

            using PrivateKey key = privateKey;
            return (new ProtectedPrivateKey(key, _config.KeyStoreDirectory, _cryptoRandom), result);
        }

        public (KeyStoreItem? KeyData, Result Result) GetKeyData(Address address)
        {
            string? keyDataJson = ReadKey(address);
            if (keyDataJson is null)
            {
                return (null, Result.Fail("Cannot find key"));
            }

            KeyStoreItem? keyData = _jsonSerializer.Deserialize<KeyStoreItem>(keyDataJson);
            return keyData is not null && HasRequiredKeyData(keyData)
                ? (keyData, Result.Success)
                : (null, Result.Fail("Cannot deserialize key"));
        }

        public (PrivateKey? PrivateKey, Result Result) GenerateKey(SecureString password)
        {
            if (!password.IsReadOnly())
            {
                throw new InvalidOperationException("Cannot work with password that is not readonly");
            }

            PrivateKey privateKey = _privateKeyGenerator.Generate();
            Result result = StoreKey(privateKey, password);
            return result.ResultType == ResultType.Success ? (privateKey, result) : (null, result);
        }

        public (ProtectedPrivateKey? PrivateKey, Result Result) GenerateProtectedKey(SecureString password)
        {
            (PrivateKey? privateKey, Result result) = GenerateKey(password);
            if (result != Result.Success || privateKey is null)
            {
                return (null, result);
            }

            using PrivateKey key = privateKey;
            return (new ProtectedPrivateKey(key, _config.KeyStoreDirectory, _cryptoRandom), result);
        }

        public Result StoreKey(Address address, KeyStoreItem keyStoreItem) => PersistKey(address, keyStoreItem);

        public Result StoreKey(Address address, byte[] keyContent, SecureString password)
        {
            if (!password.IsReadOnly())
            {
                throw new InvalidOperationException("Cannot work with password that is not readonly");
            }

            byte[] salt = _cryptoRandom.GenerateRandomBytes(32);
            byte[] passBytes = password.ToByteArray(_keyStoreEncoding);

            byte[] derivedKey = SCrypt.ComputeDerivedKey(passBytes, salt, _config.KdfparamsN, _config.KdfparamsR, _config.KdfparamsP, null, _config.KdfparamsDklen);

            byte[] encryptKey;
            string kdf = _config.Kdf;
            string cipherType = _config.Cipher;
            if (kdf == "scrypt" && cipherType == "aes-128-cbc")
            {
                encryptKey = Keccak.Compute(derivedKey.Slice(0, 16)).Bytes[..16].ToArray();
            }
            else
            {
                encryptKey = derivedKey.Take(16).ToArray();
            }

            byte[] encryptContent = keyContent;
            byte[] iv = _cryptoRandom.GenerateRandomBytes(_config.IVSize);

            byte[]? cipher = _symmetricEncrypter.Encrypt(encryptContent, encryptKey, iv, _config.Cipher);
            if (cipher is null)
            {
                return Result.Fail("Error during encryption");
            }

            Span<byte> mac = Keccak.Compute(derivedKey.Skip(_config.KdfparamsDklen - 16).Take(16).Concat(cipher).ToArray()).Bytes;

            string addressString = address.ToString(false, false);
            KeyStoreItem keyStoreItem = new()
            {
                Address = addressString,
                Crypto = new Crypto
                {
                    Cipher = _config.Cipher,
                    CipherText = cipher.ToHexString(false),
                    CipherParams = new CipherParams
                    {
                        IV = iv.ToHexString(false)
                    },
                    KDF = _config.Kdf,
                    KDFParams = new KdfParams
                    {
                        DkLen = _config.KdfparamsDklen,
                        N = _config.KdfparamsN,
                        P = _config.KdfparamsP,
                        R = _config.KdfparamsR,
                        Salt = salt.ToHexString(false)
                    },
                    MAC = mac.ToHexString(false),
                },
                Id = Guid.NewGuid().ToString(),
                Version = Version
            };

            return StoreKey(address, keyStoreItem);
        }

        public Result StoreKey(PrivateKey key, SecureString password) => StoreKey(key.Address, key.KeyBytes, password);

        public (IReadOnlyCollection<Address>? Addresses, Result Result) GetKeyAddresses()
        {
            try
            {
                string[] files = Directory.GetFiles(_keyStoreIOSettingsProvider.StoreDirectory, "UTC--*--*");
                List<Address> addresses = [];
                foreach (string file in files)
                {
                    string? fileName = Path.GetFileName(file);
                    string? addressString = fileName?.Split("--").LastOrDefault();
                    if (addressString is not null && Address.IsValidAddress(addressString, false))
                    {
                        addresses.Add(new Address(addressString));
                    }
                }

                return (addresses, Result.Success);
            }
            catch (Exception e)
            {
                string msg = "Error during getting addresses";
                if (_logger.IsError) _logger.Error(msg, e);
                return (null, Result.Fail(msg));
            }
        }

        private bool TryValidate(
            KeyStoreItem keyStoreItem,
            [NotNullWhen(true)] out string? mac,
            [NotNullWhen(true)] out string? iv,
            [NotNullWhen(true)] out string? cipherText,
            [NotNullWhen(true)] out string? salt,
            [NotNullWhen(true)] out KdfParams? kdfParams,
            [NotNullWhen(true)] out string? kdf,
            [NotNullWhen(true)] out string? cipher,
            out Result result)
        {
            mac = null;
            iv = null;
            cipherText = null;
            salt = null;
            kdfParams = null;
            kdf = null;
            cipher = null;

            if (keyStoreItem.Crypto is not
                {
                    MAC: { Length: > 0 } macValue,
                    CipherParams: { IV: { Length: > 0 } ivValue },
                    CipherText: { Length: > 0 } cipherTextValue,
                    Cipher: { Length: > 0 } cipherValue,
                    KDF: { Length: > 0 } kdfValue,
                    KDFParams: { Salt: { Length: > 0 } saltValue } kdfParamsValue,
                })
            {
                result = Result.Fail("Incorrect key");
                return false;
            }

            if (kdfParamsValue.DkLen < 16)
            {
                result = Result.Fail("Incorrect KDF parameters");
                return false;
            }

            if (keyStoreItem.Version != Version)
            {
                result = Result.Fail("KeyStore version mismatch");
                return false;
            }

            mac = macValue;
            iv = ivValue;
            cipherText = cipherTextValue;
            salt = saltValue;
            kdfParams = kdfParamsValue;
            kdf = kdfValue;
            cipher = cipherValue;

            result = Result.Success;
            return true;
        }

        private static bool HasRequiredKeyData(KeyStoreItem keyData) =>
            !string.IsNullOrWhiteSpace(keyData.Id) &&
            !string.IsNullOrWhiteSpace(keyData.Address) &&
            keyData.Crypto is not null;

        private Result PersistKey(Address address, KeyStoreItem keyData)
        {
            string serializedKey = _jsonSerializer.Serialize(keyData);

            try
            {
                string keyFileName = _keyStoreIOSettingsProvider.GetFileName(address);
                string storeDirectory = _keyStoreIOSettingsProvider.StoreDirectory;
                string path = Path.Combine(storeDirectory, keyFileName);
                File.WriteAllText(path, serializedKey, _keyStoreEncoding);
                return Result.Success;
            }
            catch (Exception e)
            {
                string msg = $"Error during persisting key for address: {address}";
                if (_logger.IsError) _logger.Error(msg, e);
                return Result.Fail(msg);
            }
        }

        public Result DeleteKey(Address address)
        {
            try
            {
                string[] files = FindKeyFiles(address);
                if (files.Length == 0)
                {
                    if (_logger.IsError) _logger.Error("Trying to internally delete key which does not exist");
                    return Result.Fail("Cannot find key");
                }

                foreach (string file in files)
                {
                    File.Delete(file);
                }

                return Result.Success;
            }
            catch (Exception e)
            {
                string msg = $"Error during deleting key for address: {address}";
                if (_logger.IsError) _logger.Error(msg, e);
                return Result.Fail(msg);
            }
        }

        private string? ReadKey(Address address)
        {
            if (address == Address.Zero)
            {
                return null;
            }

            try
            {
                string[] files = FindKeyFiles(address);
                if (files.Length == 0)
                {
                    if (_logger.IsError) _logger.Error($"A {_keyStoreIOSettingsProvider.KeyName} for address: {address} does not exists in directory {Path.GetFullPath(_keyStoreIOSettingsProvider.StoreDirectory)}.");
                    return null;
                }

                return File.ReadAllText(files[0]);
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error($"Error during reading key for address: {address}", e);
                return null;
            }
        }

        internal string[] FindKeyFiles(Address address)
        {
            string addressString = address.ToString(false, false);
            string[] files = Directory.GetFiles(_keyStoreIOSettingsProvider.StoreDirectory, $"*{addressString}*");
            return files;
        }
    }
}
