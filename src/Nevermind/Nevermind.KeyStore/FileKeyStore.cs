using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CryptSharp.Utility;
using Nevermind.Core;
using Nevermind.Core.Crypto;
using Nevermind.Json;
using Nevermind.Utils.Model;
using Random = Nevermind.Core.Crypto.Random;

namespace Nevermind.KeyStore
{
    /// <summary>
    ///     {"address":"c2d7cf95645d33006175b78989035c7c9061d3f9",
    ///     "crypto":{
    ///     "cipher":"aes-128-ctr",
    ///     "ciphertext":"0f6d343b2a34fe571639235fc16250823c6fe3bc30525d98c41dfdf21a97aedb",
    ///     "cipherparams":{
    ///     "iv":"cabce7fb34e4881870a2419b93f6c796"
    /// },
    /// "kdf":"scrypt",
    /// "kdfparams"{
    /// "dklen":32,
    /// "n":262144,
    /// "p":1,
    /// "r":8,
    /// "salt":"1af9c4a44cf45fe6fb03dcc126fa56cb0f9e81463683dd6493fb4dc76edddd51"
    /// },
    /// "mac":"5cf4012fffd1fbe41b122386122350c3825a709619224961a16e908c2a366aa6"
    /// },
    /// "id":"eddd71dd-7ad6-4cd3-bc1a-11022f7db76c",
    /// "version":3
    /// }
    /// </summary>
    public class FileKeyStore : IKeyStore
    {
        private readonly IConfigurationProvider _configurationProvider;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly ISymmetricEncrypter _symmetricEncrypter;
        private readonly ILogger _logger;

        public FileKeyStore(IConfigurationProvider configurationProvider, IJsonSerializer jsonSerializer, ISymmetricEncrypter symmetricEncrypter, ILogger logger)
        {
            _configurationProvider = configurationProvider;
            _jsonSerializer = jsonSerializer;
            _symmetricEncrypter = symmetricEncrypter;
            _logger = logger;
        }

        public int Version => 3;
        public int CryptoVersion => 1;

        public (PrivateKey, Result) GetKey(Address address, string password)
        {
            var serializedKey = ReadKey(address.ToString());
            if (serializedKey == null)
            {
                return (null, Result.Fail("Cannot find key"));
            }
            var keyStoreItem = _jsonSerializer.DeserializeObject<KeyStoreItem>(serializedKey);
            if (keyStoreItem == null)
            {
                return (null, Result.Fail("Cannot deserialize key"));
            }

            var validationResult = Validate(keyStoreItem);
            if (validationResult.ResultType != ResultType.Success)
            {
                return (null, validationResult);
            }

            var id = new Guid(keyStoreItem.Id);
            var mac = new Hex(Hex.ToBytes(keyStoreItem.Crypto.MAC));
            var iv = new Hex(Hex.ToBytes(keyStoreItem.Crypto.CipherParams.IV));
            var cipher = new Hex(Hex.ToBytes(keyStoreItem.Crypto.CipherText));
            var salt = new Hex(Hex.ToBytes(keyStoreItem.Crypto.KDFParams.Salt));

            var kdfParams = keyStoreItem.Crypto.KDFParams;
            var passBytes = _configurationProvider.KeyStoreEncoding.GetBytes(password);
            var derivedKey = SCrypt.ComputeDerivedKey(passBytes, salt, kdfParams.N, kdfParams.R, kdfParams.P, null, kdfParams.DkLen);

            var restoredMac = Keccak.Compute(derivedKey.Skip(kdfParams.DkLen - 16).Take(16).Concat(cipher.ToBytes()).ToArray()).Bytes;
            if (!mac.Equals(new Hex(restoredMac)))
            {
                return (null, Result.Fail("Incorrect MAC"));
            }
            var decryptKey = Keccak.Compute(derivedKey.Take(16).ToArray()).Bytes.Take(16).ToArray();
            var key = _symmetricEncrypter.Decrypt(cipher.ToBytes(), decryptKey, iv.ToBytes());
            if (key == null)
            {
                return (null, Result.Fail("Error during decryption"));
            }
            return (new PrivateKey(new Hex(key), id), Result.Success());
        }

        public (PrivateKey, Result) GenerateKey(string password)
        {
            var privateKey = new PrivateKey();
            var result = StoreKey(privateKey, password);
            return result.ResultType == ResultType.Success ? (privateKey, result) : (null, result);
        }

        public Result StoreKey(PrivateKey key, string password)
        {
            var salt = Random.GenerateRandomBytes(32);
            var passBytes = _configurationProvider.KeyStoreEncoding.GetBytes(password);

            var derivedKey = SCrypt.ComputeDerivedKey(passBytes, salt, _configurationProvider.KdfparamsN, _configurationProvider.KdfparamsR, _configurationProvider.KdfparamsP, null, _configurationProvider.KdfparamsDklen);

            var encryptKey = Keccak.Compute(derivedKey.Take(16).ToArray()).Bytes.Take(16).ToArray();
            var encryptContent = key.Hex.ToBytes();
            var iv = Random.GenerateRandomBytes(_configurationProvider.SymmetricEncrypterBlockSize);

            var cipher = _symmetricEncrypter.Encrypt(encryptContent, encryptKey, iv);
            if (cipher == null)
            {
                return Result.Fail("Error during encryption");
            }

            var mac = Keccak.Compute(derivedKey.Skip(_configurationProvider.KdfparamsDklen - 16).Take(16).Concat(cipher).ToArray()).Bytes;

            var address = key.Address.ToString();
            var keyStoreItem = new KeyStoreItem
            {
                Address = address,
                Crypto = new Crypto
                {
                    Cipher = _configurationProvider.Cipher,
                    CipherText = Hex.FromBytes(cipher, true),
                    CipherParams = new CipherParams
                    {
                        IV = Hex.FromBytes(iv, true)
                    },
                    KDF = _configurationProvider.Kdf,
                    KDFParams = new KDFParams
                    {
                       DkLen = _configurationProvider.KdfparamsDklen,
                       N = _configurationProvider.KdfparamsN,
                       P = _configurationProvider.KdfparamsP,
                       R = _configurationProvider.KdfparamsR,
                       Salt = Hex.FromBytes(salt, true)
                    },
                    MAC = Hex.FromBytes(mac, true),
                    Version = CryptoVersion
                },
                Id = key.Id.ToString(),
                Version = Version
            };
            var serializedKey = _jsonSerializer.SerializeObject(keyStoreItem);
            if (serializedKey == null)
            {
                return Result.Fail("Error during key serialization");
            }
            return PersistKey(address, serializedKey);
        }

        public (IEnumerable<Address>, Result) GetKeyAddresses()
        {
            try
            {
                var files = Directory.GetFiles(GetStoreDirectory());
                var addresses = files.Select(x => new Address(new Hex(Path.GetFileName(x)))).ToArray();
                return (addresses, new Result { ResultType = ResultType.Success });
            }
            catch (Exception e)
            {
                var msg = "Error during getting addresses";
                _logger.Error(msg, e);
                return (null, Result.Fail(msg));
            }
        }

        public Result DeleteKey(Address address, string password)
        {
            var key = GetKey(address, password);
            if (key.Item2.ResultType == ResultType.Failure)
            {
                return Result.Fail("Cannot find key");
            }
            return DeleteKey(address.ToString());
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
            if (keyStoreItem.Crypto.Version != CryptoVersion)
            {
                return Result.Fail("Crypto version mismatch");
            }
            return Result.Success();
        }

        private string GetStoreDirectory()
        {
            var directory = _configurationProvider.KeyStoreDirectory;
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            return directory;
        }

        private Result PersistKey(string address, string serializedKey)
        {
            try
            {
                var path = Path.Combine(GetStoreDirectory(), address);
                File.WriteAllText(path, serializedKey, _configurationProvider.KeyStoreEncoding);
                return new Result {ResultType = ResultType.Success};
            }
            catch (Exception e)
            {
                var msg = $"Error during persisting key for address: {address}";
                _logger.Error(msg, e);
                return Result.Fail(msg);
            }
        }

        private Result DeleteKey(string address)
        {
            try
            {
                var path = Path.Combine(GetStoreDirectory(), address);
                if (!File.Exists(path))
                {
                    _logger.Error("Trying to internally delete key which does not exist");
                    return Result.Fail("Cannot find key");
                }
                File.Delete(path);
                return new Result { ResultType = ResultType.Success };
            }
            catch (Exception e)
            {
                var msg = $"Error during deleting key for address: {address}";
                _logger.Error(msg, e);
                return Result.Fail(msg);
            }
        }

        private string ReadKey(string address)
        {
            try
            {
                var path = Path.Combine(GetStoreDirectory(), address);
                if (!File.Exists(path))
                {
                    _logger.Error($"A private key for address: {address} does not exists.");
                    return null;
                }
                return File.ReadAllText(path);
            }
            catch (Exception e)
            {
                _logger.Error($"Error during reading key for address: {address}", e);
                return null;
            }
        }
    }
}