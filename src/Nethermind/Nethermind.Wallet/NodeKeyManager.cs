// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO.Abstractions;
using System.Security;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.KeyStore;
using Nethermind.KeyStore.Config;
using Nethermind.Logging;

namespace Nethermind.Wallet
{
    public class NodeKeyManager(
        ICryptoRandom cryptoRandom,
        IKeyStore keyStore,
        IKeyStoreConfig config,
        ILogManager logManager,
        IPasswordProvider passwordProvider,
        IFileSystem fileSystem) : INodeKeyManager
    {
        public const string UnsecuredNodeKeyFilePath = "node.key.plain";

        private readonly ICryptoRandom _cryptoRandom = cryptoRandom ?? throw new ArgumentNullException(nameof(cryptoRandom));
        private readonly IKeyStore _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
        private readonly IKeyStoreConfig _config = config ?? throw new ArgumentNullException(nameof(config));
        private readonly ILogger _logger = logManager?.GetClassLogger<NodeKeyManager>() ?? throw new ArgumentNullException(nameof(logManager));
        private readonly IPasswordProvider _passwordProvider = passwordProvider ?? throw new ArgumentNullException(nameof(passwordProvider));
        private readonly IFileSystem _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));

        /// <summary>
        /// https://stackoverflow.com/questions/1344221/how-can-i-generate-random-alphanumeric-strings
        /// Note this is not a perfect solution - to be used for the next few months as a next step in securing th enode key.
        /// </summary>
        /// <param name="size"></param>
        /// <returns></returns>
        [DoNotUseInSecuredContext("This is not a strong security for the node key - just a minor protection against JSON RPC use for the node key")]
        private SecureString CreateNodeKeyPassword(int size)
        {
            char[] chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ1234567890".ToCharArray();
            byte[] data = _cryptoRandom.GenerateRandomBytes(size);
            SecureString secureString = new();
            for (int i = 0; i < data.Length; i++)
            {
                secureString.AppendChar(chars[data[i] % chars.Length]);
            }

            secureString.MakeReadOnly();
            return secureString;
        }

        [DoNotUseInSecuredContext("This stored the node key in plaintext - it is just one step further to the full node key protection")]
        public ProtectedPrivateKey LoadNodeKey()
        {
            ProtectedPrivateKey LoadKeyFromFile()
            {
                string oldPath = UnsecuredNodeKeyFilePath.GetApplicationResourcePath();
                string newPath = (_config.EnodeKeyFile ?? UnsecuredNodeKeyFilePath).GetApplicationResourcePath(_config.KeyStoreDirectory);
                GenerateKeyIfNeeded(newPath, oldPath);
                using PrivateKey privateKey = new(_fileSystem.File.ReadAllBytes(newPath));
                return new ProtectedPrivateKey(privateKey, _config.KeyStoreDirectory, _cryptoRandom);
            }

            void GenerateKeyIfNeeded(string newFile, string oldFile)
            {
                if (!_fileSystem.File.Exists(newFile))
                {
                    if (_logger.IsInfo) _logger.Info("Generating private key for the node (no node key in configuration) - stored in plain + key store for JSON RPC unlocking");
                    using PrivateKeyGenerator privateKeyGenerator = new(_cryptoRandom);
                    PrivateKey nodeKey = _fileSystem.File.Exists(oldFile) ? new PrivateKey(_fileSystem.File.ReadAllBytes(oldFile)) : privateKeyGenerator.Generate();
                    string keyStoreDirectory = _config.KeyStoreDirectory.GetApplicationResourcePath();
                    _fileSystem.Directory.CreateDirectory(keyStoreDirectory);
                    _fileSystem.File.WriteAllBytes(newFile, nodeKey.KeyBytes);
                    SecureString nodeKeyPassword = CreateNodeKeyPassword(8);
                    _keyStore.StoreKey(nodeKey, nodeKeyPassword);
                    if (_logger.IsInfo) _logger.Info("Store this password for unlocking the node key for JSON RPC - this is not secure - this log message will be in your log files. Use only in DEV contexts.");
                }
            }

            if (_config.TestNodeKey is not null)
                return new ProtectedPrivateKey(new PrivateKey(_config.TestNodeKey), _config.KeyStoreDirectory, _cryptoRandom);

            ProtectedPrivateKey key = LoadKeyForAccount(_config.EnodeAccount);
            return key ?? LoadKeyFromFile();
        }

        public ProtectedPrivateKey LoadSignerKey() => LoadKeyForAccount(_config.BlockAuthorAccount) ?? LoadNodeKey();

        private ProtectedPrivateKey LoadKeyForAccount(string account)
        {
            if (!string.IsNullOrEmpty(account))
            {
                Address blockAuthor = new(Bytes.FromHexString(account));
                SecureString password = _passwordProvider.GetPassword(blockAuthor);

                try
                {
                    (ProtectedPrivateKey privateKey, Result result) = _keyStore.GetProtectedKey(new Address(account), password);
                    if (result == Result.Success)
                    {
                        return privateKey;
                    }

                    if (_logger.IsError) _logger.Error($"Not able to unlock the key for {account} due to error: '{result.Error}'");
                    // continue to the other methods
                }
                catch (Exception e)
                {
                    if (_logger.IsError) _logger.Error($"Not able to unlock the key for {account}", e);
                }
            }

            return null;
        }
    }
}
