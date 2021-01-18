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

[assembly:InternalsVisibleTo("Nethermind.KeyStore.Test")]
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
            _keyStoreEncoding = _config.KeyStoreEncoding.Equals("UTF-8", StringComparison.InvariantCultureIgnoreCase)
                ? new UTF8Encoding(false)
                : Encoding.GetEncoding(_config.KeyStoreEncoding);
            _privateKeyGenerator = new PrivateKeyGenerator(_cryptoRandom);
            _keyStoreIOSettingsProvider = keyStoreIOSettingsProvider ?? throw new ArgumentNullException(nameof(keyStoreIOSettingsProvider));
        }

        public int Version => 3;
        public int CryptoVersion => 1;

        public (KeyStoreItem KeyData, Result Result) Verify(string keyJson)
        {
            try
            {
                KeyStoreItem keyData = _jsonSerializer.Deserialize<KeyStoreItem>(keyJson);
                return (keyData, Result.Success);
            }
            catch (Exception)
            {                
                return (null, Result.Fail("Invalid key data format"));
            }
        }

        public (byte[] Key, Result Result) GetKeyBytes(Address address, SecureString password)
        {
            if (!password.IsReadOnly())
            {
                throw new InvalidOperationException("Cannot work with password that is not readonly");
            }

            var serializedKey = ReadKey(address);
            if (serializedKey == null)
            {
                return (null, Result.Fail("Cannot find key"));
            }
            var keyStoreItem = _jsonSerializer.Deserialize<KeyStoreItem>(serializedKey);
            if (keyStoreItem?.Crypto == null)
            {
                return (null, Result.Fail("Cannot deserialize key"));
            }

            var validationResult = Validate(keyStoreItem);
            if (validationResult.ResultType != ResultType.Success)
            {
                return (null, validationResult);
            }

            byte[] mac = Bytes.FromHexString(keyStoreItem.Crypto.MAC);
            byte[] iv = Bytes.FromHexString(keyStoreItem.Crypto.CipherParams.IV);
            byte[] cipher = Bytes.FromHexString(keyStoreItem.Crypto.CipherText);
            byte[] salt = Bytes.FromHexString(keyStoreItem.Crypto.KDFParams.Salt);

            var kdfParams = keyStoreItem.Crypto.KDFParams;
            var passBytes = password.ToByteArray(_keyStoreEncoding);

            byte[] derivedKey;
            var kdf = keyStoreItem.Crypto.KDF.Trim();
            switch (kdf)
            {
                case "scrypt":
                    int r = kdfParams.R.Value;
                    int p = kdfParams.P.Value;
                    int n = kdfParams.N.Value;
                    derivedKey = SCrypt.ComputeDerivedKey(passBytes, salt, n, r, p, null, kdfParams.DkLen);
                    break;
                case "pbkdf2":
                    int c = kdfParams.C.Value;
                    var deriveBytes = new Rfc2898DeriveBytes(passBytes, salt, kdfParams.C.Value, HashAlgorithmName.SHA256);
                    derivedKey = deriveBytes.GetBytes(256);
                    break;
                default:
                    return (null, Result.Fail($"Unsupported algoritm: {kdf}"));
            }

            var restoredMac = Keccak.Compute(derivedKey.Slice(kdfParams.DkLen - 16, 16).Concat(cipher).ToArray()).Bytes;
            if (!Bytes.AreEqual(mac, restoredMac))
            {
                return (null, Result.Fail("Incorrect MAC"));
            }

            var cipherType = keyStoreItem.Crypto.Cipher.Trim();
            byte[] decryptKey;
            if (kdf == "scrypt" && cipherType == "aes-128-cbc")
            {
                decryptKey = Keccak.Compute(derivedKey.Slice(0, 16)).Bytes.Slice(0, 16);
            }
            else
            {
                decryptKey = derivedKey.Slice(0, 16);
            }

            byte[] key = _symmetricEncrypter.Decrypt(cipher, decryptKey, iv, cipherType);
            if (key == null)
            {
                return (null, Result.Fail("Error during decryption"));
            }

            // TODO: maybe only allow to sign here so the key never leaves the area?
            return (key, Result.Success);
        }

        public (PrivateKey PrivateKey, Result Result) GetKey(Address address, SecureString password)
        {
            var geyKeyResult = GetKeyBytes(address, password);
            if (geyKeyResult.Result.ResultType == ResultType.Failure)
            {
                return (null, geyKeyResult.Result);
            }
            return (new PrivateKey(geyKeyResult.Key), geyKeyResult.Result);
        }

        public (ProtectedPrivateKey PrivateKey, Result Result) GetProtectedKey(Address address, SecureString password)
        {
            (PrivateKey privateKey, Result result) = GetKey(address, password);
            using var key = privateKey;
            return (result == Result.Success ? new ProtectedPrivateKey(key, _cryptoRandom) : null, result);
        }

        public (KeyStoreItem KeyData, Result Result) GetKeyData(Address address)
        {
            string keyDataJson = ReadKey(address);
            return (_jsonSerializer.Deserialize<KeyStoreItem>(keyDataJson), Result.Success);
        }

        public (PrivateKey PrivateKey, Result Result) GenerateKey(SecureString password)
        {
            if (!password.IsReadOnly())
            {
                throw new InvalidOperationException("Cannot work with password that is not readonly");
            }
            
            var privateKey = _privateKeyGenerator.Generate();
            var result = StoreKey(privateKey, password);
            return result.ResultType == ResultType.Success ? (privateKey, result) : (null, result);
        }

        public (ProtectedPrivateKey PrivateKey, Result Result) GenerateProtectedKey(SecureString password)
        {
            (PrivateKey privateKey, Result result) = GenerateKey(password);
            using var key = privateKey;
            return (result == Result.Success ? new ProtectedPrivateKey(key, _cryptoRandom) : null, result);
        }

        public Result StoreKey(Address address, KeyStoreItem keyStoreItem)
        {
            return PersistKey(address, keyStoreItem);
        }

        public Result StoreKey(Address address, byte[] keyContent, SecureString password)
        {
            if (!password.IsReadOnly())
            {
                throw new InvalidOperationException("Cannot work with password that is not readonly");
            }

            var salt = _cryptoRandom.GenerateRandomBytes(32);
            var passBytes = password.ToByteArray(_keyStoreEncoding);

            var derivedKey = SCrypt.ComputeDerivedKey(passBytes, salt, _config.KdfparamsN, _config.KdfparamsR, _config.KdfparamsP, null, _config.KdfparamsDklen);

            byte[] encryptKey;
            var kdf = _config.Kdf;
            var cipherType = _config.Cipher;
            if (kdf == "scrypt" && cipherType == "aes-128-cbc")
            {
                encryptKey = Keccak.Compute(derivedKey.Slice(0, 16)).Bytes.Slice(0, 16);
            }
            else
            {
                encryptKey = derivedKey.Take(16).ToArray();
            }

            var encryptContent = keyContent;
            var iv = _cryptoRandom.GenerateRandomBytes(_config.IVSize);

            var cipher = _symmetricEncrypter.Encrypt(encryptContent, encryptKey, iv, _config.Cipher);
            if (cipher == null)
            {
                return Result.Fail("Error during encryption");
            }

            var mac = Keccak.Compute(derivedKey.Skip(_config.KdfparamsDklen - 16).Take(16).Concat(cipher).ToArray()).Bytes;

            string addressString = address.ToString(false, false);
            var keyStoreItem = new KeyStoreItem
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

        public Result StoreKey(PrivateKey key, SecureString password)
        {
            return StoreKey(key.Address, key.KeyBytes, password);
        }

        public (IReadOnlyCollection<Address> Addresses, Result Result) GetKeyAddresses()
        {
            try
            {
                var files = Directory.GetFiles(_keyStoreIOSettingsProvider.StoreDirectory, "UTC--*--*");
                var addresses = files.Select(Path.GetFileName).Select(fn => fn.Split("--").LastOrDefault()).Where(x => Address.IsValidAddress(x, false)).Select(x => new Address(x)).ToArray();
                return (addresses, new Result { ResultType = ResultType.Success });
            }
            catch (Exception e)
            {
                var msg = "Error during getting addresses";
                if(_logger.IsError) _logger.Error(msg, e);
                return (null, Result.Fail(msg));
            }
        }

        private Result Validate(KeyStoreItem keyStoreItem)
        {
            if (keyStoreItem.Crypto?.CipherParams == null || keyStoreItem.Crypto.KDFParams == null)
            {
                return Result.Fail("Incorrect key");
            }
            
            if (keyStoreItem.Version != Version)
            {
                return Result.Fail("KeyStore version mismatch");
            }
            
            return Result.Success;
        }

        private Result PersistKey(Address address, KeyStoreItem keyData)
        {
            var serializedKey = _jsonSerializer.Serialize(keyData);
            
            try
            {
                var keyFileName = _keyStoreIOSettingsProvider.GetFileName(address);
                var storeDirectory = _keyStoreIOSettingsProvider.StoreDirectory;
                var path = Path.Combine(storeDirectory, keyFileName);
                File.WriteAllText(path, serializedKey, _keyStoreEncoding);
                return new Result {ResultType = ResultType.Success};
            }
            catch (Exception e)
            {
                var msg = $"Error during persisting key for address: {address}";
                if(_logger.IsError) _logger.Error(msg, e);
                return Result.Fail(msg);
            }
        }

        public Result DeleteKey(Address address)
        {
            try
            {
                var files = FindKeyFiles(address);
                if (files.Length == 0)
                {
                    if(_logger.IsError) _logger.Error("Trying to internally delete key which does not exist");
                    return Result.Fail("Cannot find key");
                }

                foreach (string file in files)
                {
                    File.Delete(file);
                }
                
                return new Result { ResultType = ResultType.Success };
            }
            catch (Exception e)
            {
                var msg = $"Error during deleting key for address: {address}";
                if(_logger.IsError) _logger.Error(msg, e);
                return Result.Fail(msg);
            }
        }
        
        private string ReadKey(Address address)
        {
            if (address == Address.Zero)
            {
                return null;
            }
            
            try
            {
                var files = FindKeyFiles(address);
                if (files.Length == 0)
                {
                    if(_logger.IsError) _logger.Error($"A {_keyStoreIOSettingsProvider.KeyName} for address: {address} does not exists in directory {Path.GetFullPath(_keyStoreIOSettingsProvider.StoreDirectory)}.");
                    return null;
                }
                
                return File.ReadAllText(files[0]);
            }
            catch (Exception e)
            {
                if(_logger.IsError) _logger.Error($"Error during reading key for address: {address}", e);
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
