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
using System.Security;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.KeyStore;
using Nethermind.KeyStore.Config;
using Nethermind.Logging;

namespace Nethermind.Wallet
{
    public class NodeKeyManager : INodeKeyManager
    {
        private const string UnsecuredNodeKeyFilePath = "node.key.plain";

        private readonly ICryptoRandom _cryptoRandom;
        private readonly IKeyStore _keyStore;
        private readonly IKeyStoreConfig _config;
        private readonly ILogger _logger;

        public NodeKeyManager(ICryptoRandom cryptoRandom, IKeyStore keyStore, IKeyStoreConfig config, ILogManager logManager)
        {
            _cryptoRandom = cryptoRandom ?? throw new ArgumentNullException(nameof(cryptoRandom));
            _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        /// <summary>
        /// https://stackoverflow.com/questions/1344221/how-can-i-generate-random-alphanumeric-strings
        /// Note this is not a perfect solution - to be used for the next few months as a next step in securing th enode key.
        /// </summary>
        /// <param name="size"></param>
        /// <returns></returns>
        [DoNotUseInSecuredContext("This is not a strong security for the node key - just a minor protection against JSON RPC use for the node key")]
        private SecureString CreateNodeKeyPassword(int size)
        {
            char[] chars =
                "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ1234567890".ToCharArray();
            byte[] data = _cryptoRandom.GenerateRandomBytes(size);
            SecureString secureString = new SecureString();
            for (int i = 0; i < data.Length; i++)
            {
                secureString.AppendChar(chars[data[i] % chars.Length]);
            }

            secureString.MakeReadOnly();
            return secureString;
        }
        
        [DoNotUseInSecuredContext("This stored the node key in plaintext - it is just one step further to the full node key protection")]
        public PrivateKey LoadNodeKey()
        {
            // this is not secure at all but this is just the node key, nothing critical so far, will use the key store here later and allow to manage by password when launching the node
            if (_config.TestNodeKey == null)
            {
                string oldPath = UnsecuredNodeKeyFilePath.GetApplicationResourcePath();
                string newPath = UnsecuredNodeKeyFilePath.GetApplicationResourcePath(_config.KeyStoreDirectory);
                
                if (!File.Exists(newPath))
                {
                    if (_logger.IsInfo) _logger.Info("Generating private key for the node (no node key in configuration) - stored in plain + key store for JSON RPC unlocking");
                    PrivateKey nodeKey = File.Exists(oldPath) ? new PrivateKey(File.ReadAllBytes(oldPath)) : new PrivateKeyGenerator(_cryptoRandom).Generate();
                    var keyStoreDirectory = _config.KeyStoreDirectory.GetApplicationResourcePath();
                    Directory.CreateDirectory(keyStoreDirectory);
                    File.WriteAllBytes(newPath, nodeKey.KeyBytes);
                    SecureString nodeKeyPassword = CreateNodeKeyPassword(8);
                    _keyStore.StoreKey(nodeKey, nodeKeyPassword);
                    if(_logger.IsInfo) _logger.Info("Store this password for unlocking the node key for JSON RPC - this is not secure - this log message will be in your log files. Use only in DEV contexts.");
                    if(_logger.IsInfo) _logger.Info(nodeKeyPassword.Unsecure());
                }


                return new PrivateKey(File.ReadAllBytes(newPath));
            }

            return new PrivateKey(_config.TestNodeKey);
        }
    }
}