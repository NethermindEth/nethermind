// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading.Tasks;
using FastEnumUtility;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Crypto;
using Nethermind.KeyStore;
using Nethermind.KeyStore.Config;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Wallet.Test;

[Parallelizable(ParallelScope.All)]
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
                        ProtectedKeyStoreWallet wallet = new(
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

        public void Dispose() => _keyStorePath?.Dispose();
    }

    private readonly ConcurrentDictionary<WalletType, Context> _cachedWallets = new();
    private readonly ConcurrentQueue<Context> _wallets = new();

    [OneTimeSetUp]
    public void Setup() =>
        // by pre-caching wallets we make the tests do lot less work
        Parallel.ForEach(WalletTypes, walletType =>
        {
            Context cachedWallet = new(walletType);
            _cachedWallets.TryAdd(walletType, cachedWallet);
            _wallets.Enqueue(cachedWallet);
        });

    [OneTimeTearDown]
    public void TearDown() => Parallel.ForEach(_wallets, static wallet =>
    {
        wallet.Dispose();
    });

    public enum WalletType
    {
        KeyStore,
        Memory,
        ProtectedKeyStore
    }

    public static IEnumerable<WalletType> WalletTypes => FastEnum.GetValues<WalletType>();

    [Test]
    public void Key_store_wallet_reports_address_enumeration_failure()
    {
        IKeyStore keyStore = Substitute.For<IKeyStore>();
        keyStore.GetKeyAddresses().Returns((Array.Empty<Address>(), Result.Fail("unavailable")));
        DevKeyStoreWallet wallet = new(keyStore, LimboLogs.Instance, createTestAccounts: false);

        Assert.That(wallet.GetAccounts, Throws.TypeOf<InvalidOperationException>().With.Message.EqualTo("unavailable"));
    }

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
            PrivateKey key = new(keyBytes);
            TestContext.Out.Write(key.Address.Bytes.ToHexString() + Environment.NewLine);
            Assert.That(ctx.Wallet.GetAccounts().Any(a => a == key.Address), Is.True, $"{i}");
        }

        Assert.That(ctx.Wallet.GetAccounts().Length, Is.EqualTo(count));
    }

    [Test]
    public void Can_sign_on_networks_with_chain_id([ValueSource(nameof(WalletTypes))] WalletType walletType, [Values(0ul, 1ul, 40000ul, ulong.MaxValue / 3)] ulong chainId)
    {
        EthereumEcdsa ecdsa = new(chainId);
        Context ctx = _cachedWallets[walletType];
        for (int i = 1; i <= (walletType == WalletType.Memory ? 10 : 3); i++)
        {
            Address signerAddress = ctx.Wallet.GetAccounts()[0];
            Transaction tx = new();
            tx.SenderAddress = signerAddress;

            bool signed = ctx.Wallet.TrySignTransaction(tx, chainId);
            Assert.That(signed, Is.True, $"wallet should sign tx for unlocked account {signerAddress}");
            Address recovered = ecdsa.RecoverAddress(tx);
            Assert.That(recovered, Is.EqualTo(signerAddress), $"{i}");
            Console.WriteLine(tx.Signature);
            Assert.That(tx.Signature.ChainId, Is.EqualTo(chainId), "chainId");
        }
    }

    [TestCase(WalletType.KeyStore, false, TestName = "KeyStore, locked")]
    [TestCase(WalletType.KeyStore, true, TestName = "KeyStore, unlocked")]
    [TestCase(WalletType.ProtectedKeyStore, false, TestName = "ProtectedKeyStore, locked")]
    [TestCase(WalletType.ProtectedKeyStore, true, TestName = "ProtectedKeyStore, unlocked")]
    public void HasKey_WhenKeyFileIsMissingFromAccountList_FindsItAsUnlockingDoes(WalletType walletType, bool unlocked)
    {
        using TempPath directory = TempPath.GetTempDirectory();
        using SecureString passphrase = "passphrase".Secure();
        KeyStoreConfig config = new() { KeyStoreDirectory = directory.Path, KdfparamsN = 1024 };
        FileKeyStore keyStore = new(config, new EthereumJsonSerializer(), new AesEncrypter(config, LimboLogs.Instance),
            new CryptoRandom(), LimboLogs.Instance, new PrivateKeyStoreIOSettingsProvider(config));
        keyStore.StoreKey(TestItem.PrivateKeyD, passphrase);
        File.Move(Directory.GetFiles(directory.Path).Single(), Path.Combine(directory.Path, $"{TestItem.AddressD.ToString(false, false)}.json"));
        IWallet wallet = walletType == WalletType.KeyStore
            ? new DevKeyStoreWallet(keyStore, LimboLogs.Instance, createTestAccounts: false)
            : new ProtectedKeyStoreWallet(keyStore, new ProtectedPrivateKeyFactory(new CryptoRandom(), Timestamper.Default, directory.Path),
                Timestamper.Default, LimboLogs.Instance);
        Assert.That(wallet.GetAccounts(), Does.Not.Contain(TestItem.AddressD), "precondition: the account list only names UTC key files");
        if (unlocked)
            Assert.That(wallet.UnlockAccount(TestItem.AddressD, passphrase), Is.True, "precondition: unlocking finds the key file");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(wallet.HasKey(TestItem.AddressD), Is.True, "the key file is found by the lookup unlocking uses");
            Assert.That(wallet.HasKey(TestItem.AddressE), Is.False, "no key file is named after this account");
        }
    }

    [Test]
    public void HasKey_WhenWalletKeepsTheDefault_AnswersFromTheAccountList()
    {
        IWallet wallet = _cachedWallets[WalletType.Memory].Wallet;
        Address listed = wallet.GetAccounts()[0];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(wallet.HasKey(listed), Is.True, "the account list names this account");
            Assert.That(wallet.HasKey(TestItem.AddressE), Is.False, "the account list does not name this account");
            Assert.That(((IWallet)NullWallet.Instance).HasKey(TestItem.AddressE), Is.False, "a wallet without accounts holds no key");
        }
    }
}
