// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Security;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.KeyStore;
using Nethermind.KeyStore.Config;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Wallet.Test
{
    [Parallelizable(ParallelScope.All)]
    public class NodeKeyManagerTests
    {
        [Test]
        public void LoadNodeKey_loads_TestNodeKey()
        {
            NodeKeyManagerTest test = CreateTest();
            test.KeyStoreConfig.TestNodeKey = TestItem.PrivateKeyA.ToString();
            test.NodeKeyManager.LoadNodeKey().Unprotect().Should().Be(TestItem.PrivateKeyA);
        }

        [Test]
        public void LoadNodeKey_loads_key_for_EnodeAccount()
        {
            NodeKeyManagerTest test = CreateTest();
            test.KeyStoreConfig.EnodeAccount = TestItem.AddressA.ToString();
            test.PasswordProvider.GetPassword(TestItem.AddressA).Returns("p1".Secure());
            test.KeyStore.GetProtectedKey(TestItem.AddressA, Arg.Any<SecureString>()).Returns(
                c => ((SecureString)c[1]).Unsecure() == "p1"
                    ? (new ProtectedPrivateKey(TestItem.PrivateKeyA, Path.Combine("testKeyStoreDir", Path.GetRandomFileName())), Result.Success)
                    : ((ProtectedPrivateKey)null, Result.Fail("nope")));
            test.NodeKeyManager.LoadNodeKey().Unprotect().Should().Be(TestItem.PrivateKeyA);
        }

        [TestCase(null)]
        [TestCase("testFile")]
        public void LoadNodeKey_creates_file(string filePath)
        {
            NodeKeyManagerTest test = CreateTest();
            test.KeyStoreConfig.EnodeKeyFile = filePath;
            test.CryptoRandom.GenerateRandomBytes(32).Returns(TestItem.PrivateKeyA.KeyBytes);
            filePath ??= NodeKeyManager.UnsecuredNodeKeyFilePath;
            filePath = filePath.GetApplicationResourcePath(test.KeyStoreConfig.KeyStoreDirectory);
            test.FileSystem.File.ReadAllBytes(filePath).Returns(TestItem.PrivateKeyA.KeyBytes);
            PrivateKey nodeKey = test.NodeKeyManager.LoadNodeKey().Unprotect();
            nodeKey.Should().Be(TestItem.PrivateKeyA);
            test.FileSystem.File.Received().WriteAllBytes(filePath, Arg.Is<byte[]>(a => a.SequenceEqual(nodeKey.KeyBytes)));
        }

        [TestCase(null)]
        [TestCase("testFile")]
        public void LoadNodeKey_loads_file(string filePath)
        {
            NodeKeyManagerTest test = CreateTest();
            test.KeyStoreConfig.EnodeKeyFile = filePath;
            filePath ??= NodeKeyManager.UnsecuredNodeKeyFilePath;
            filePath = filePath.GetApplicationResourcePath(test.KeyStoreConfig.KeyStoreDirectory);
            test.FileSystem.File.ReadAllBytes(filePath).Returns(TestItem.PrivateKeyA.KeyBytes);
            test.FileSystem.File.Exists(filePath).Returns(true);
            PrivateKey nodeKey = test.NodeKeyManager.LoadNodeKey().Unprotect();
            nodeKey.Should().Be(TestItem.PrivateKeyA);
            test.FileSystem.File.DidNotReceive().WriteAllBytes(filePath, nodeKey.KeyBytes);
        }

        [Test]
        public void LoadSignerKey_defaults_to_LoadNodeKey()
        {
            NodeKeyManagerTest test = CreateTest();
            test.KeyStoreConfig.TestNodeKey = TestItem.PrivateKeyA.ToString();
            test.NodeKeyManager.LoadSignerKey().Unprotect().Should().Be(TestItem.PrivateKeyA);
        }

        [Test]
        public void LoadSignerKey_loads_key_for_BlockAuthorAccount()
        {
            NodeKeyManagerTest test = CreateTest();
            test.KeyStoreConfig.BlockAuthorAccount = TestItem.AddressA.ToString();
            test.PasswordProvider.GetPassword(TestItem.AddressA).Returns("p1".Secure());
            test.KeyStore.GetProtectedKey(TestItem.AddressA, Arg.Any<SecureString>()).Returns(
                c => ((SecureString)c[1]).Unsecure() == "p1"
                    ? (new ProtectedPrivateKey(TestItem.PrivateKeyA, Path.Combine("testKeyStoreDir", Path.GetRandomFileName())), Result.Success)
                    : ((ProtectedPrivateKey)null, Result.Fail("nope")));
            test.NodeKeyManager.LoadSignerKey().Unprotect().Should().Be(TestItem.PrivateKeyA);
        }

        private NodeKeyManagerTest CreateTest()
        {
            IKeyStore keyStore = Substitute.For<IKeyStore>();
            ICryptoRandom cryptoRandom = Substitute.For<ICryptoRandom>();
            KeyStoreConfig keyStoreConfig = new KeyStoreConfig();
            IPasswordProvider passwordProvider = Substitute.For<IPasswordProvider>();
            IFileSystem fileSystem = Substitute.For<IFileSystem>();

            return new NodeKeyManagerTest()
            {
                NodeKeyManager = new NodeKeyManager(cryptoRandom, keyStore, keyStoreConfig, LimboLogs.Instance, passwordProvider, fileSystem),
                KeyStore = keyStore,
                CryptoRandom = cryptoRandom,
                KeyStoreConfig = keyStoreConfig,
                PasswordProvider = passwordProvider,
                FileSystem = fileSystem
            };
        }

        private class NodeKeyManagerTest
        {
            public NodeKeyManager NodeKeyManager { get; set; }
            public IKeyStore KeyStore { get; set; }
            public ICryptoRandom CryptoRandom { get; set; }
            public KeyStoreConfig KeyStoreConfig { get; set; }
            public IPasswordProvider PasswordProvider { get; set; }
            public IFileSystem FileSystem { get; set; }
        }

    }
}
