// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Security;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.JsonRpc.Modules.Personal;
using Nethermind.KeyStore;
using Nethermind.Logging;
using Nethermind.Wallet;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules
{
    [Parallelizable(ParallelScope.None)]
    [TestFixture]
    public class PersonalRpcModuleTests
    {
        [SetUp]
        public void Initialize()
        {
            _wallet = new DevWallet(new WalletConfig(), LimboLogs.Instance);
            _ecdsa = new EthereumEcdsa(TestBlockchainIds.ChainId);
            _keyStore = Substitute.For<IKeyStore>();
        }

        private IKeyStore _keyStore = null!;
        private IEcdsa _ecdsa = null!;
        private DevWallet _wallet = null!;

        [Test]
        public async Task Personal_list_accounts()
        {
            IPersonalRpcModule rpcModule = new PersonalRpcModule(_ecdsa, _wallet, _keyStore);
            string serialized = await RpcTest.TestSerializedRequest(rpcModule, "personal_listAccounts");
            string expectedAccounts = string.Join(',', _wallet.GetAccounts().Select(static a => $"\"{a}\""));
            Assert.That(serialized, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"result\":[{expectedAccounts}],\"id\":67}}"));
        }

        [Test]
        public async Task Personal_import_raw_key()
        {
            Address expectedAddress = new("707Fc13C0eB628c074f7ff514Ae21ACaeE0ec072");
            PrivateKey privateKey = new("a8fceb14d53045b1c8baedf7bc1f38b2540ce132ac28b1ec8b93b8113165abc0");
            string passphrase = "testPass";
            IPersonalRpcModule rpcModule = new PersonalRpcModule(_ecdsa, _wallet, _keyStore);
            string serialized = await RpcTest.TestSerializedRequest(rpcModule, "personal_importRawKey", privateKey.KeyBytes.ToHexString(), passphrase);
            Assert.That(serialized, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"result\":\"{expectedAddress}\",\"id\":67}}"));
            _keyStore.DeleteKey(expectedAddress);
        }

        [Test]
        public async Task Personal_new_account()
        {
            int accountsBefore = _wallet.GetAccounts().Length;
            string passphrase = "testPass";
            IPersonalRpcModule rpcModule = new PersonalRpcModule(_ecdsa, _wallet, _keyStore);
            string serialized = await RpcTest.TestSerializedRequest(rpcModule, "personal_newAccount", passphrase);
            Address[] accountsNow = _wallet.GetAccounts();
            Assert.That(accountsNow.Length, Is.EqualTo(accountsBefore + 1), "length");
            Assert.That(serialized, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"result\":\"{accountsNow.Last()}\",\"id\":67}}"));
        }

        [Test]
        public async Task Personal_sign_with_passphrase_does_not_unlock_account()
        {
            string passphrase = "testPass";
            Address address = _wallet.NewAccount(passphrase.Secure());
            _wallet.LockAccount(address);
            Assert.That(_wallet.IsUnlocked(address), Is.False, "precondition: account starts locked");

            IPersonalRpcModule rpcModule = new PersonalRpcModule(_ecdsa, _wallet, _keyStore);

            string signed = await RpcTest.TestSerializedRequest(rpcModule, "personal_sign", "0xdeadbeef", address.ToString(), passphrase);
            Assert.That(signed, Does.Contain("\"result\""));
            Assert.That(_wallet.IsUnlocked(address), Is.False, "passphrase sign must not persistently unlock the account");

            string withoutPassphrase = await RpcTest.TestSerializedRequest(rpcModule, "personal_sign", "0xdeadbeef", address.ToString());
            Assert.That(withoutPassphrase, Does.Contain("\"error\""));
        }

        [Test]
        public async Task Personal_sign_with_passphrase_on_keystore_wallet_preserves_lock_and_rejects_wrong_passphrase()
        {
            const string privateKeyHex = "a8fceb14d53045b1c8baedf7bc1f38b2540ce132ac28b1ec8b93b8113165abc0";
            const string correctPassphrase = "correct";
            Address address = new PrivateKey(privateKeyHex).Address;

            IKeyStore keyStore = Substitute.For<IKeyStore>();
            keyStore.GetKey(address, Arg.Any<SecureString>())
                .Returns<(PrivateKey, Result)>(ci => ci.Arg<SecureString>().Unsecure() == correctPassphrase
                    ? (new PrivateKey(privateKeyHex), Result.Success)
                    : (null!, Result.Fail("authentication failed")));

            DevKeyStoreWallet wallet = new(keyStore, LimboLogs.Instance, createTestAccounts: false);
            IPersonalRpcModule rpcModule = new PersonalRpcModule(_ecdsa, wallet, keyStore);

            string signed = await RpcTest.TestSerializedRequest(rpcModule, "personal_sign", "0xdeadbeef", address.ToString(), correctPassphrase);
            Assert.That(signed, Does.Contain("\"result\""));
            Assert.That(wallet.IsUnlocked(address), Is.False, "passphrase sign must not unlock the keystore account");

            string wrongPassphrase = await RpcTest.TestSerializedRequest(rpcModule, "personal_sign", "0xdeadbeef", address.ToString(), "wrong");
            Assert.That(wrongPassphrase, Does.Contain("\"error\""));

            string withoutPassphrase = await RpcTest.TestSerializedRequest(rpcModule, "personal_sign", "0xdeadbeef", address.ToString());
            Assert.That(withoutPassphrase, Does.Contain("\"error\""));
        }

        [Test]
        [Ignore("Cannot reproduce GO signing yet")]
        public async Task Personal_ec_sign()
        {
            IPersonalRpcModule rpcModule = new PersonalRpcModule(_ecdsa, _wallet, _keyStore);
            string serialized = await RpcTest.TestSerializedRequest(rpcModule, "personal_sign", "0xdeadbeaf", "0x9b2055d370f73ec7d8a03e965129118dc8f5bf83");
            Assert.That(serialized, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"result\":\"0xa3f20717a250c2b0b729b7e5becbff67fdaef7e0699da4de7ca5895b02a170a12d887fd3b17bfdce3481f10bea41f45ba9f709d39ce8325427b57afcfc994cee1b\"}}"));
        }

        [Test]
        [Ignore("Cannot reproduce GO signing yet")]
        public async Task Personal_ec_recover()
        {
            IPersonalRpcModule rpcModule = new PersonalRpcModule(_ecdsa, _wallet, _keyStore);
            string serialized = await RpcTest.TestSerializedRequest(rpcModule, "personal_ecRecover", "0xdeadbeaf", "0xa3f20717a250c2b0b729b7e5becbff67fdaef7e0699da4de7ca5895b02a170a12d887fd3b17bfdce3481f10bea41f45ba9f709d39ce8325427b57afcfc994cee1b");
            Assert.That(serialized, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"result\":\"0x9b2055d370f73ec7d8a03e965129118dc8f5bf83\"}}"));
        }

        [TestCase("0x00", Description = "Too short (1 byte)")]
        [TestCase("0x0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000001b00", Description = "Too long (66 bytes)")]
        public async Task Personal_ecRecover_rejects_invalid_signature_length(string signature)
        {
            IPersonalRpcModule rpcModule = new PersonalRpcModule(_ecdsa, _wallet, _keyStore);
            string serialized = await RpcTest.TestSerializedRequest(rpcModule, "personal_ecRecover", "0xdeadbeaf", signature);
            Assert.That(serialized, Does.Contain("\"error\""));
            Assert.That(serialized, Does.Contain("Invalid signature length"));
        }
    }
}
