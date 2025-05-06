// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.KeyStore;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.ExternalSigner.Plugin
{
    internal class ClefKeystore : IKeyStore
    {
        public int Version => throw new NotImplementedException();

        public int CryptoVersion => throw new NotImplementedException();

        public Result DeleteKey(Address address)
        {
            throw new NotImplementedException();
        }

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
            if (serializedKey is null)
            {
                return (null, Result.Fail("Cannot find key"));
            }
            var keyStoreItem = _jsonSerializer.Deserialize<KeyStoreItem>(serializedKey);
            if (keyStoreItem?.Crypto is null)
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
                    // ComputeDerivedKey uses too little stack size in case of multithread processing, which may cause stack overflow.
                    // Switch to single thread if "cost" is too high, see Scrypt.ThreadSMixCalls internals
                    derivedKey = SCrypt.ComputeDerivedKey(passBytes, salt, n, r, p, n > 8192 ? 1 : null, kdfParams.DkLen);
                    break;
                case "pbkdf2":
                    int c = kdfParams.C.Value;
                    var deriveBytes = new Rfc2898DeriveBytes(passBytes, salt, c, HashAlgorithmName.SHA256);
                    derivedKey = deriveBytes.GetBytes(256);
                    break;
                default:
                    return (null, Result.Fail($"Unsupported algorithm: {kdf}"));
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
                decryptKey = Keccak.Compute(derivedKey.Slice(0, 16)).Bytes[..16].ToArray();
            }
            else
            {
                decryptKey = derivedKey.Slice(0, 16);
            }

            byte[] key = _symmetricEncrypter.Decrypt(cipher, decryptKey, iv, cipherType);
            if (key is null)
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
            return (result == Result.Success ? new ProtectedPrivateKey(key, _config.KeyStoreDirectory, _cryptoRandom) : null, result);
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
            return (result == Result.Success ? new ProtectedPrivateKey(key, _config.KeyStoreDirectory, _cryptoRandom) : null, result);
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
                encryptKey = Keccak.Compute(derivedKey.Slice(0, 16)).Bytes[..16].ToArray();
            }
            else
            {
                encryptKey = derivedKey.Take(16).ToArray();
            }

            var encryptContent = keyContent;
            var iv = _cryptoRandom.GenerateRandomBytes(_config.IVSize);

            var cipher = _symmetricEncrypter.Encrypt(encryptContent, encryptKey, iv, _config.Cipher);
            if (cipher is null)
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
                var addresses = files.Select(Path.GetFileName).Select(static fn => fn.Split("--").LastOrDefault()).Where(static x => Address.IsValidAddress(x, false)).Select(static x => new Address(x)).ToArray();
                return (addresses, Result.Success);
            }
            catch (Exception e)
            {
                var msg = "Error during getting addresses";
                if (_logger.IsError) _logger.Error(msg, e);
                return (null, Result.Fail(msg));
            }
        }

        private Result Validate(KeyStoreItem keyStoreItem)
        {
            if (keyStoreItem.Crypto?.CipherParams is null || keyStoreItem.Crypto.KDFParams is null)
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
                return Result.Success;
            }
            catch (Exception e)
            {
                var msg = $"Error during persisting key for address: {address}";
                if (_logger.IsError) _logger.Error(msg, e);
                return Result.Fail(msg);
            }
        }

        public Result DeleteKey(Address address) => ThrowNotImplemented();


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

        [DoesNotReturn]
        [StackTraceHidden]
        private static void ThrowNotImplemented([CallerMemberName] string member = "") => throw new NotImplementedException($"Clef remote signer does not support '{member}'");
    }
}
