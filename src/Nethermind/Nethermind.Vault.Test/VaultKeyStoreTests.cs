// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.Security;
using System.Text;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.KeyStore;
using Nethermind.KeyStore.Config;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Vault.Config;
using Nethermind.Vault.KeyStore;
using NUnit.Framework;

namespace Nethermind.Vault.Test
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class VaultKeyStoreTests
    {
        private FileKeyStore _store;
        private IJsonSerializer _serializer;
        private IKeyStoreConfig _keyStoreConfig;
        private ICryptoRandom _cryptoRandom;
        private IKeyStoreIOSettingsProvider _keyStoreIOSettingsProvider;
        private readonly Address _address = TestItem.AddressA;

        [SetUp]
        public void Initialize()
        {
            var config = new VaultConfig();
            _keyStoreConfig = new KeyStoreConfig();
            _keyStoreIOSettingsProvider = new VaultKeyStoreIOSettingsProvider(config);

            ILogManager logger = LimboLogs.Instance;
            _serializer = new EthereumJsonSerializer();
            _cryptoRandom = new CryptoRandom();
            _store = new FileKeyStore(_keyStoreConfig, _serializer, new AesEncrypter(_keyStoreConfig, logger), _cryptoRandom, logger, _keyStoreIOSettingsProvider);
        }

        [TestCase("fragile potato army dinner inch enrich decline under scrap soup audit worth trend point cheese sand online parrot faith catch olympic dignity mail crouch")]
        public void Can_generate_Vault_key_store_file(string keyTestCase)
        {
            SecureString securePassword = new SecureString();
            string password = "testpuppeth";
            securePassword = password.Secure();

            GenerateVaultKeyStoreFile(securePassword, _address, keyTestCase);

            var file = FindFileByAddress(_address);
            Assert.True(File.Exists(file));
        }


        [TestCase("test")]
        [TestCase("fragile potato army dinner inch enrich decline under scrap soup audit worth trend point cheese sand online parrot faith catch olympic dignity mail crouch")]
        [TestCase("  fragile potato army dinner inch enrich decline under scrap soup audit worth trend point cheese sand online parrot faith catch olympic dignity mail crouch    ")]
        public void Can_store_key_and_read_the_same_key(string keyTestCase)
        {
            SecureString securePassword = new SecureString();
            string password = "testpuppeth";
            securePassword = password.Secure();

            GenerateVaultKeyStoreFile(securePassword, _address, keyTestCase);
            var result = _store.GetKeyBytes(_address, securePassword);
            var keyFromKeyStore = Encoding.UTF8.GetString(result.Key);

            Assert.AreEqual(keyFromKeyStore, keyTestCase);
        }

        [TearDown]
        public void TearDown()
        {
            _store.DeleteKey(_address);
        }

        private void GenerateVaultKeyStoreFile(SecureString password, Address address, string keyToStore)
        {
            byte[] keyBytes = Encoding.UTF8.GetBytes(keyToStore);
            _store.StoreKey(_address, keyBytes, password);
        }

        private string FindFileByAddress(Address address)
        {
            string addressString = address.ToString(false, false);
            string[] files = Directory.GetFiles(_keyStoreIOSettingsProvider.StoreDirectory, $"*Vault_*{addressString}*");
            return files[0];
        }
    }
}
