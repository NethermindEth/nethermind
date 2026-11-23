// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable annotations

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CryptSharp.Utility;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
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
        private const string Scrypt = "scrypt";
        private const string Pbkdf2 = "pbkdf2";
        private const string Aes128Ctr = "aes-128-ctr";
        private const string Aes128Cbc = "aes-128-cbc";

        // New key files take the MAC key from the last 16 derived bytes; the Web3 Secret Storage definition takes bytes
        // 16..31. The two agree only at 32, so that is the only length new key files are written with.
        private const int WriteDkLen = 32;
        private const int MinWriteSaltLength = 16;
        // Reading needs derived bytes 0..15 for the cipher key and 16 bytes for the MAC key (see IsMacValid).
        private const int MinReadDkLen = 16;
        private const int MaxReadDkLen = 1024;
        private const int MacKeyLength = 16;

        // Resource limits for scrypt on read and write, far above the parameters wallets use. Memory is
        // 128 * r * (n + 2p + 3) bytes: n + 3 blocks of 128 * r bytes for the mixing and the p input blocks, held twice.
        // Work is n * r * p, which the default settings put at 1 << 21.
        private const long MaxScryptMemory = 1L << 31;
        private const long MaxScryptWork = 1L << 24;
        private const string ScryptLimits = "scrypt may use at most 2 GiB of memory and n * r * p of at most 16777216";

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
                KeyStoreItem keyData = DeserializeKeyStoreItem(keyJson);
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

            Result validationResult = Validate(keyStoreItem);
            if (validationResult.ResultType != ResultType.Success)
            {
                return (null, validationResult);
            }

            byte[] mac = Bytes.FromHexString(keyStoreItem.Crypto.MAC);
            byte[] iv = Bytes.FromHexString(keyStoreItem.Crypto.CipherParams.IV);
            byte[] cipher = Bytes.FromHexString(keyStoreItem.Crypto.CipherText);
            byte[] salt = Bytes.FromHexString(keyStoreItem.Crypto.KDFParams.Salt);

            KdfParams kdfParams = keyStoreItem.Crypto.KDFParams;
            int dkLen = kdfParams.DkLen;
            if (dkLen is < MinReadDkLen or > MaxReadDkLen)
            {
                return (null, Result.Fail($"Unsupported dklen: {dkLen}"));
            }

            byte[] passBytes = password.ToByteArray(_keyStoreEncoding);

            byte[] derivedKey;
            string? kdf = ToCanonicalName(keyStoreItem.Crypto.KDF);
            switch (kdf)
            {
                case Scrypt:
                    if (kdfParams.N is not int n || kdfParams.R is not int r || kdfParams.P is not int p
                        || !IsValidScryptN(n) || r <= 0 || p <= 0)
                    {
                        return (null, Result.Fail("Incorrect key: scrypt requires a power-of-two n above 1 and positive r and p"));
                    }

                    if (!IsWithinScryptLimits(n, r, p))
                    {
                        return (null, Result.Fail($"Unsupported scrypt parameters: n {n}, r {r}, p {p}; {ScryptLimits}"));
                    }

                    derivedKey = DeriveScryptKey(passBytes, salt, n, r, p, dkLen);
                    break;
                case Pbkdf2:
                    if (kdfParams.C is not int c || c <= 0)
                    {
                        return (null, Result.Fail("Incorrect key: pbkdf2 requires a positive c"));
                    }

                    derivedKey = Rfc2898DeriveBytes.Pbkdf2(passBytes, salt, c, HashAlgorithmName.SHA256, dkLen);
                    break;
                default:
                    return (null, Result.Fail($"Unsupported algorithm: {kdf}"));
            }

            if (!IsMacValid(mac, derivedKey, cipher))
            {
                return (null, Result.Fail("Incorrect MAC"));
            }

            string? cipherType = ToCanonicalName(keyStoreItem.Crypto.Cipher);
            byte[] decryptKey;
            if (kdf == Scrypt && cipherType == Aes128Cbc)
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

        public (PrivateKey? PrivateKey, Result Result) GetKey(Address address, SecureString password)
        {
            (byte[]? keyBytes, Result result) = GetKeyBytes(address, password);
            if (result.ResultType == ResultType.Failure)
            {
                return (null, result);
            }

            return (new PrivateKey(keyBytes ?? throw new InvalidOperationException("Successful key decryption returned no key bytes.")), result);
        }

        public (ProtectedPrivateKey? PrivateKey, Result Result) GetProtectedKey(Address address, SecureString password)
        {
            (PrivateKey? privateKey, Result result) = GetKey(address, password);
            if (result.ResultType != ResultType.Success)
            {
                return (null, result);
            }

            using PrivateKey key = privateKey
                ?? throw new InvalidOperationException("Successful key retrieval returned no private key.");
            return (new ProtectedPrivateKey(key, _config.KeyStoreDirectory, _cryptoRandom), result);
        }

        public (KeyStoreItem? KeyData, Result Result) GetKeyData(Address address)
        {
            string? keyDataJson = ReadKey(address);
            if (keyDataJson is null)
            {
                return (null, Result.Fail("Cannot find key"));
            }

            return (DeserializeKeyStoreItem(keyDataJson), Result.Success);
        }

        private KeyStoreItem DeserializeKeyStoreItem(string json) =>
            _jsonSerializer.Deserialize<KeyStoreItem>(json)
            ?? throw new JsonException("Key data decoded as null.");

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
            if (result.ResultType != ResultType.Success)
            {
                return (null, result);
            }

            using PrivateKey key = privateKey
                ?? throw new InvalidOperationException("Successful key generation returned no private key.");
            return (new ProtectedPrivateKey(key, _config.KeyStoreDirectory, _cryptoRandom), result);
        }

        public Result StoreKey(Address address, KeyStoreItem keyStoreItem) => PersistKey(address, keyStoreItem);

        public Result StoreKey(Address address, byte[] keyContent, SecureString password)
        {
            if (!password.IsReadOnly())
            {
                throw new InvalidOperationException("Cannot work with password that is not readonly");
            }

            // Each setting is read once, so the key is derived, encrypted and described with the same values.
            string? kdf = ToCanonicalName(_config.Kdf);
            string? cipherType = ToCanonicalName(_config.Cipher);
            int dkLen = _config.KdfparamsDklen;
            int saltLength = _config.KdfparamsSaltLen;
            int n = _config.KdfparamsN;
            int r = _config.KdfparamsR;
            int p = _config.KdfparamsP;
            ValidateWriteSettings(kdf, cipherType, dkLen, saltLength, n, r, p);

            byte[] salt = _cryptoRandom.GenerateRandomBytes(saltLength);
            byte[] passBytes = password.ToByteArray(_keyStoreEncoding);

            byte[] derivedKey = DeriveScryptKey(passBytes, salt, n, r, p, dkLen);

            byte[] encryptKey;
            if (kdf == Scrypt && cipherType == Aes128Cbc)
            {
                encryptKey = Keccak.Compute(derivedKey.Slice(0, 16)).Bytes[..16].ToArray();
            }
            else
            {
                encryptKey = derivedKey.Take(16).ToArray();
            }

            byte[] encryptContent = keyContent;
            byte[] iv = _cryptoRandom.GenerateRandomBytes(_config.IVSize);

            byte[] cipher = _symmetricEncrypter.Encrypt(encryptContent, encryptKey, iv, cipherType);
            if (cipher is null)
            {
                return Result.Fail("Error during encryption");
            }

            Span<byte> mac = Keccak.Compute(derivedKey.Skip(dkLen - 16).Take(16).Concat(cipher).ToArray()).Bytes;

            string addressString = address.ToString(false, false);
            KeyStoreItem keyStoreItem = new()
            {
                Address = addressString,
                Crypto = new Crypto
                {
                    Cipher = cipherType,
                    CipherText = cipher.ToHexString(false),
                    CipherParams = new CipherParams
                    {
                        IV = iv.ToHexString(false)
                    },
                    KDF = kdf,
                    KDFParams = new KdfParams
                    {
                        DkLen = dkLen,
                        N = n,
                        P = p,
                        R = r,
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

        public (IReadOnlyCollection<Address> Addresses, Result Result) GetKeyAddresses()
        {
            try
            {
                string[] files = Directory.GetFiles(_keyStoreIOSettingsProvider.StoreDirectory, "UTC--*--*");
                Address[] addresses = files.Select(Path.GetFileName).Select(static fn => fn.Split("--").LastOrDefault()).Where(static x => Address.IsValidAddress(x, false)).Select(static x => new Address(x)).ToArray();
                return (addresses, Result.Success);
            }
            catch (Exception e)
            {
                string msg = "Error during getting addresses";
                if (_logger.IsError) LogIoFailure(msg, e);
                return ([], Result.Fail(msg));
            }
        }

        public bool HasKey(Address address)
        {
            if (address == Address.Zero)
            {
                return false;
            }

            try
            {
                return FindKeyFiles(address).Length > 0;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return false;
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

        private static string? ToCanonicalName(string? name) => name?.Trim().ToLowerInvariant();

        // Scrypt needs a cost above 1.
        private static bool IsValidScryptN(int n) => n > 1 && int.IsPow2(n);

        // For p above 1 the library would start extra threads with an 8 KB stack, on which the runtime can overflow and
        // end the process. On one thread the p passes run on the caller's stack, one after another, and give the same key.
        private static byte[] DeriveScryptKey(byte[] password, byte[] salt, int n, int r, int p, int dkLen) =>
            SCrypt.ComputeDerivedKey(password, salt, n, r, p, 1, dkLen);

        /// <summary>Whether scrypt with these parameters stays within the memory and work a key file may ask for.</summary>
        /// <remarks>Expects positive <paramref name="r"/> and <paramref name="p"/>. Divides the limits, so nothing overflows.</remarks>
        internal static bool IsWithinScryptLimits(int n, int r, int p) =>
            n + 2L * p + 3 <= MaxScryptMemory / (128L * r) && (long)n * r <= MaxScryptWork / p;

        // Accepts the MAC key of the Web3 Secret Storage definition, derived bytes 16..31, and, for a dklen other than 32,
        // the last 16 derived bytes, which Nethermind key files with such a dklen use.
        private static bool IsMacValid(ReadOnlySpan<byte> mac, ReadOnlySpan<byte> derivedKey, ReadOnlySpan<byte> cipher)
        {
            int length = MacKeyLength + cipher.Length;
            Span<byte> macBody = length <= 256 ? stackalloc byte[length] : new byte[length];
            cipher.CopyTo(macBody[MacKeyLength..]);

            if (derivedKey.Length >= 2 * MacKeyLength)
            {
                derivedKey.Slice(MacKeyLength, MacKeyLength).CopyTo(macBody);
                if (MacMatches(mac, macBody))
                {
                    return true;
                }

                if (derivedKey.Length == 2 * MacKeyLength)
                {
                    return false;
                }
            }

            derivedKey[^MacKeyLength..].CopyTo(macBody);
            return MacMatches(mac, macBody);

            static bool MacMatches(ReadOnlySpan<byte> mac, ReadOnlySpan<byte> macBody)
            {
                ValueHash256 restoredMac = ValueKeccak.Compute(macBody);
                return CryptographicOperations.FixedTimeEquals(mac, restoredMac.Bytes);
            }
        }

        /// <summary>Rejects settings that new key files cannot be written with.</summary>
        /// <remarks>
        /// Checked on every write rather than at startup, so settings that only affect writing do not stop a node from
        /// reading its key files. Throws rather than failing the <see cref="Result"/>, which most callers do not check.
        /// </remarks>
        /// <exception cref="InvalidConfigurationException">A setting is not supported for new key files.</exception>
        private static void ValidateWriteSettings(string? kdf, string? cipherType, int dkLen, int saltLength, int n, int r, int p)
        {
            if (kdf != Scrypt)
            {
                throw UnsupportedWriteSetting(nameof(IKeyStoreConfig.Kdf), kdf, $"use '{Scrypt}'; key files derived with '{Pbkdf2}' can still be read");
            }

            if (cipherType is not (Aes128Ctr or Aes128Cbc))
            {
                throw UnsupportedWriteSetting(nameof(IKeyStoreConfig.Cipher), cipherType, $"use '{Aes128Ctr}' or '{Aes128Cbc}'");
            }

            if (dkLen != WriteDkLen)
            {
                throw UnsupportedWriteSetting(nameof(IKeyStoreConfig.KdfparamsDklen), dkLen, $"use {WriteDkLen}");
            }

            if (saltLength < MinWriteSaltLength)
            {
                throw UnsupportedWriteSetting(nameof(IKeyStoreConfig.KdfparamsSaltLen), saltLength, $"use at least {MinWriteSaltLength}");
            }

            // The same rules as for reading a scrypt key file.
            if (!IsValidScryptN(n))
            {
                throw UnsupportedWriteSetting(nameof(IKeyStoreConfig.KdfparamsN), n, "use a power of two greater than 1");
            }

            if (r <= 0)
            {
                throw UnsupportedWriteSetting(nameof(IKeyStoreConfig.KdfparamsR), r, "use at least 1");
            }

            if (p <= 0)
            {
                throw UnsupportedWriteSetting(nameof(IKeyStoreConfig.KdfparamsP), p, "use at least 1");
            }

            if (!IsWithinScryptLimits(n, r, p))
            {
                throw new InvalidConfigurationException(
                    $"KeyStore.{nameof(IKeyStoreConfig.KdfparamsN)} '{n}', KeyStore.{nameof(IKeyStoreConfig.KdfparamsR)} '{r}' and KeyStore.{nameof(IKeyStoreConfig.KdfparamsP)} '{p}' are not supported together for new key files: {ScryptLimits}.",
                    ExitCodes.ForbiddenOptionValue);
            }

            static InvalidConfigurationException UnsupportedWriteSetting(string setting, object? value, string rule) =>
                new($"KeyStore.{setting} '{value}' is not supported for new key files: {rule}.", ExitCodes.ForbiddenOptionValue);
        }

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
                if (_logger.IsError) LogIoFailure(msg, e);
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
                if (_logger.IsError) LogIoFailure(msg, e);
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
                    if (_logger.IsError) _logger.Error($"A {_keyStoreIOSettingsProvider.KeyName} for address: {address} does not exists in directory {Path.GetFullPath(_keyStoreIOSettingsProvider.StoreDirectory):hide}.");
                    return null;
                }

                return File.ReadAllText(files[0]);
            }
            catch (Exception e)
            {
                if (_logger.IsError) LogIoFailure($"Error during reading key for address: {address}", e);
                return null;
            }
        }

        private void LogIoFailure(string message, Exception error)
        {
            bool maskSensitiveData = SensitiveLogMasking.Enabled;
            _logger.Error(maskSensitiveData ? $"{message}: {error.GetType().Name}" : message, maskSensitiveData ? null : error);
        }

        internal string[] FindKeyFiles(Address address)
        {
            string addressString = address.ToString(false, false);
            string[] files = Directory.GetFiles(_keyStoreIOSettingsProvider.StoreDirectory, $"*{addressString}*");
            return files;
        }
    }
}
