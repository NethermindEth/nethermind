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
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Json;
using Nethermind.Core.Specs;
using Nethermind.KeyStore;
using Nethermind.KeyStore.Config;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Wallet.Test
{
    [TestFixture]
    public class DevWalletTests
    {
        public enum DevWalletType
        {
            KeyStore,
            Memory
        }

        [SetUp]
        public void SetUp()
        {
            DeleteTestKeyStore();
        }

        private void DeleteTestKeyStore()
        {
            if (Directory.Exists(_keyStorePath))
            {
                Directory.Delete(_keyStorePath, true);
            }
        }

        [TearDown]
        public void TearDown()
        {
            DeleteTestKeyStore();
        }

        private string _keyStorePath = Path.Combine(Path.GetTempPath(), "DevWalletTests_keystore");

        private IWallet SetupWallet(DevWalletType devWalletType)
        {
            switch (devWalletType)
            {
                case DevWalletType.KeyStore:
                    IKeyStoreConfig config = new KeyStoreConfig();
                    config.KeyStoreDirectory = _keyStorePath;
                    ISymmetricEncrypter encrypter = new AesEncrypter(config, LimboLogs.Instance);
                    return new DevKeyStoreWallet(
                        new FileKeyStore(config, new EthereumJsonSerializer(), encrypter, new CryptoRandom(), LimboLogs.Instance),
                        LimboLogs.Instance);
                case DevWalletType.Memory:
                    return new DevWallet(new WalletConfig(), LimboLogs.Instance);
                default:
                    throw new ArgumentOutOfRangeException(nameof(devWalletType), devWalletType, null);
            }
        }

        [TestCase(DevWalletType.KeyStore)]
        [TestCase(DevWalletType.Memory)]
        public void Can_setup_wallet_twice(DevWalletType walletType)
        {
            IWallet wallet1 = SetupWallet(walletType);
            IWallet wallet2 = SetupWallet(walletType);
        }
        
        [TestCase(DevWalletType.KeyStore)]
        [TestCase(DevWalletType.Memory)]
        public void Has_10_dev_accounts(DevWalletType walletType)
        {
            IWallet wallet = SetupWallet(walletType);
            Assert.AreEqual((walletType == DevWalletType.Memory ? 10 : 3), wallet.GetAccounts().Length);
        }
        
        [TestCase(DevWalletType.KeyStore)]
        [TestCase(DevWalletType.Memory)]
        public void Each_account_can_sign_with_simple_key(DevWalletType walletType)
        {
            IWallet wallet = SetupWallet(walletType);

            int count = walletType == DevWalletType.Memory ? 10 : 3;
            for (int i = 1; i <= count; i++)
            {
                byte[] keyBytes = new byte[32];
                keyBytes[31] = (byte) i;
                PrivateKey key = new PrivateKey(keyBytes);
                TestContext.Write(key.Address.Bytes.ToHexString() + Environment.NewLine);
                Assert.True(wallet.GetAccounts().Any(a => a == key.Address), $"{i}");
            }
            
            Assert.AreEqual(count, wallet.GetAccounts().Length);
        }

        [TestCase(DevWalletType.KeyStore)]
        [TestCase(DevWalletType.Memory)]
        public void Can_sign_on_networks_with_chain_id(DevWalletType walletType)
        {
            const int networkId = 40000;
            EthereumEcdsa ecdsa = new EthereumEcdsa(new SingleReleaseSpecProvider(Latest.Release, networkId), NullLogManager.Instance);
            IWallet wallet = SetupWallet(walletType);

            for (int i = 1; i <= (walletType == DevWalletType.Memory ? 10 : 3); i++)
            {
                Address signerAddress = wallet.GetAccounts()[0];
                Transaction tx = new Transaction();
                tx.SenderAddress = signerAddress;
                
                wallet.Sign(tx, networkId);
                Address recovered = ecdsa.RecoverAddress(tx, networkId);
                Assert.AreEqual(signerAddress, recovered, $"{i}");
                Assert.AreEqual(networkId, tx.Signature.ChainId, "chainId");
            }
        }
    }
}