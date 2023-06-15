// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FastEnumUtility;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.IO;
using Nethermind.Crypto;
using Nethermind.KeyStore;
using Nethermind.KeyStore.Config;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Wallet.Test
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class WalletTests
    {
        private class Context : IDisposable
        {
            private readonly TempPath _keyStorePath = TempPath.GetTempDirectory();

            public IWallet Wallet { get; }

            public Context(WalletType walletType)
            {
                switch (walletType)
                {
                    case WalletType.KeyStore:
                        {
                            IKeyStoreConfig config = new KeyStoreConfig();
                            config.KeyStoreDirectory = _keyStorePath.Path;
                            ISymmetricEncrypter encrypter = new AesEncrypter(config, LimboLogs.Instance);
                            Wallet = new DevKeyStoreWallet(
                                new FileKeyStore(config, new EthereumJsonSerializer(), encrypter, new CryptoRandom(),
                                    LimboLogs.Instance, new PrivateKeyStoreIOSettingsProvider(config)),
                                LimboLogs.Instance);
                            break;
                        }
                    case WalletType.Memory:
                        {
                            Wallet = new DevWallet(new WalletConfig(), LimboLogs.Instance);
                            break;
                        }
                    case WalletType.ProtectedKeyStore:
                        {
                            IKeyStoreConfig config = new KeyStoreConfig();
                            config.KeyStoreDirectory = _keyStorePath.Path;
                            ISymmetricEncrypter encrypter = new AesEncrypter(config, LimboLogs.Instance);
                            ProtectedKeyStoreWallet wallet = new ProtectedKeyStoreWallet(
                                new FileKeyStore(config, new EthereumJsonSerializer(), encrypter, new CryptoRandom(),
                                    LimboLogs.Instance, new PrivateKeyStoreIOSettingsProvider(config)),
                                new ProtectedPrivateKeyFactory(new CryptoRandom(),
                                    Timestamper.Default, config.KeyStoreDirectory),
                                Timestamper.Default,
                                LimboLogs.Instance);
                            wallet.SetupTestAccounts(3);

                            Wallet = wallet;
                            break;
                        }
                    default:
                        throw new ArgumentOutOfRangeException(nameof(walletType), walletType, null);
                }
            }

            public void Dispose()
            {
                _keyStorePath?.Dispose();
            }
        }

        private readonly ConcurrentDictionary<WalletType, Context> _cachedWallets = new ConcurrentDictionary<WalletType, Context>();
        private readonly ConcurrentQueue<Context> _wallets = new();

        [OneTimeSetUp]
        public void Setup()
        {
            // by pre-caching wallets we make the tests do lot less work
            Parallel.ForEach(WalletTypes.Union(WalletTypes), walletType =>
            {
                Context cachedWallet = new Context(walletType);
                _cachedWallets.TryAdd(walletType, cachedWallet);
                _wallets.Enqueue(cachedWallet);
            });
        }

        [OneTimeTearDown]
        public void TearDown()
        {
            Parallel.ForEach(_wallets, wallet =>
            {
                wallet.Dispose();
            });
        }

        public enum WalletType
        {
            KeyStore,
            Memory,
            ProtectedKeyStore
        }

        public static IEnumerable<WalletType> WalletTypes => FastEnum.GetValues<WalletType>();

        [Test]
        public void Has_10_dev_accounts([ValueSource(nameof(WalletTypes))] WalletType walletType)
        {
            Context ctx = _cachedWallets[walletType];
            Assert.That(ctx.Wallet.GetAccounts().Length, Is.EqualTo((walletType == WalletType.Memory ? 10 : 3)));
        }

        [Test]
        public void Each_account_can_sign_with_simple_key([ValueSource(nameof(WalletTypes))] WalletType walletType)
        {
            Context ctx = _cachedWallets[walletType];
            int count = walletType == WalletType.Memory ? 10 : 3;
            for (int i = 1; i <= count; i++)
            {
                byte[] keyBytes = new byte[32];
                keyBytes[31] = (byte)i;
                PrivateKey key = new PrivateKey(keyBytes);
                TestContext.Write(key.Address.Bytes.ToHexString() + Environment.NewLine);
                Assert.True(ctx.Wallet.GetAccounts().Any(a => a == key.Address), $"{i}");
            }

            Assert.That(ctx.Wallet.GetAccounts().Length, Is.EqualTo(count));
        }

        [Test]
        public void Can_sign_on_networks_with_chain_id([ValueSource(nameof(WalletTypes))] WalletType walletType, [Values(0ul, 1ul, 40000ul, ulong.MaxValue / 3)] ulong chainId)
        {
            EthereumEcdsa ecdsa = new EthereumEcdsa(chainId, LimboLogs.Instance);
            Context ctx = _cachedWallets[walletType];
            for (int i = 1; i <= (walletType == WalletType.Memory ? 10 : 3); i++)
            {
                Address signerAddress = ctx.Wallet.GetAccounts()[0];
                Transaction tx = new Transaction();
                tx.SenderAddress = signerAddress;

                WalletExtensions.Sign(ctx.Wallet, tx, chainId);
                Address recovered = ecdsa.RecoverAddress(tx);
                Assert.That(recovered, Is.EqualTo(signerAddress), $"{i}");
                Console.WriteLine(tx.Signature);
                Assert.That(tx.Signature.ChainId, Is.EqualTo(chainId), "chainId");
            }
        }
    }
}
