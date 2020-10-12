//  Copyright (c) 2018 Demerzel Solutions Limited
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
using System.Security;
using System.Text;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.KeyStore.Config;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.KeyStore.Test
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class VaultKeyStoreTests
    {
        private FileKeyStore _store;
        private IJsonSerializer _serializer;
        private IKeyStoreConfig _keyStoreConfig;
        private ICryptoRandom _cryptoRandom;
        private SecureString _testPasswordSecured;
        private SecureString _wrongPasswordSecured;
        private readonly string _testPassword = "testpassword";
        private readonly Address _address = TestItem.AddressA;

        [SetUp]
        public void Initialize()
        {
            _keyStoreConfig = new KeyStoreConfig();
            var keyStoreIOSettingsProvider = Substitute.For<IKeyStoreIOSettingsProvider>();
            var vaultKeyStore = "vaultkeystore";
            var directory = vaultKeyStore.GetApplicationResourcePath();
            
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            keyStoreIOSettingsProvider.StoreDirectory.Returns(directory);
            DateTime utcNow = DateTime.UtcNow;
            //var str = $"UTC--{utcNow:yyyy-MM-dd}T{utcNow:HH-mm-ss.ffffff}000Z--{address.ToString(false, false)}";
            keyStoreIOSettingsProvider.GetFileName(Arg.Any<Address>()).Returns(
                args => ((Address)args[0]).ToString());
            _testPasswordSecured = new SecureString();
            _wrongPasswordSecured = new SecureString();

            for (int i = 0; i < _testPassword.Length; i++)
            {
                _testPasswordSecured.AppendChar(_testPassword[i]);
                _wrongPasswordSecured.AppendChar('*');
            }

            _testPasswordSecured.MakeReadOnly();
            _wrongPasswordSecured.MakeReadOnly();

            ILogManager logger = LimboLogs.Instance;
            _serializer = new EthereumJsonSerializer();
            _cryptoRandom = new CryptoRandom();
            _store = new FileKeyStore(_keyStoreConfig, _serializer, new AesEncrypter(_keyStoreConfig, logger), _cryptoRandom, logger, keyStoreIOSettingsProvider);
        }


        [TestCase("test")]
        public void Can_store_string_key(string keyTestCase)
        {
            SecureString securePassword = new SecureString();
            string password = "testpuppeth";
            for (int i = 0; i < password.Length; i++)
            {
                securePassword.AppendChar(password[i]);
            }

            securePassword.MakeReadOnly();
            byte[] keyBytes = Encoding.UTF8.GetBytes(keyTestCase);

            _store.StoreKey(_address, keyBytes, securePassword);
            var result = _store.GetKeyBytes(_address, securePassword);
            var keyFromKeyStore = System.Text.Encoding.UTF8.GetString(result.Key);
            Assert.AreEqual(keyFromKeyStore, keyTestCase);
        }

        [TearDown]
        public void TearDown()
        {
            _store.DeleteKey(_address);
        }

    }
}
