//  Copyright (c) 2021 Demerzel Solutions Limited
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

using System.Linq;
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
            _wallet = new DevWallet(new WalletConfig(),  LimboLogs.Instance);
            _ecdsa = new EthereumEcdsa(ChainId.Mainnet, LimboLogs.Instance);
            _keyStore = Substitute.For<IKeyStore>();
        }

        private IKeyStore _keyStore;
        private IEcdsa _ecdsa;
        private DevWallet _wallet;

        [Test]
        public void Personal_list_accounts()
        {
            IPersonalRpcModule rpcModule = new PersonalRpcModule(_ecdsa, _wallet, _keyStore);
            string serialized = RpcTest.TestSerializedRequest(rpcModule, "personal_listAccounts");
            string expectedAccounts = string.Join(',', _wallet.GetAccounts().Select(a => $"\"{a.ToString()}\""));
            Assert.AreEqual($"{{\"jsonrpc\":\"2.0\",\"result\":[{expectedAccounts}],\"id\":67}}", serialized);
        }
        
        [Test]
        public void Personal_import_raw_key()
        {
            Address expectedAddress = new Address( "707Fc13C0eB628c074f7ff514Ae21ACaeE0ec072");
            PrivateKey privateKey = new PrivateKey("a8fceb14d53045b1c8baedf7bc1f38b2540ce132ac28b1ec8b93b8113165abc0");
            string passphrase = "testPass";
            IPersonalRpcModule rpcModule = new PersonalRpcModule(_ecdsa, _wallet, _keyStore);
            string serialized = RpcTest.TestSerializedRequest( rpcModule, "personal_importRawKey", privateKey.KeyBytes.ToHexString(), passphrase);
            Assert.AreEqual($"{{\"jsonrpc\":\"2.0\",\"result\":\"{expectedAddress.ToString()}\",\"id\":67}}", serialized);
            _keyStore.DeleteKey(expectedAddress);
        }
        
        [Test]
        public void Personal_new_account()
        {
            int accountsBefore = _wallet.GetAccounts().Length;
            string passphrase = "testPass";
            IPersonalRpcModule rpcModule = new PersonalRpcModule(_ecdsa, _wallet, _keyStore);
            string serialized = RpcTest.TestSerializedRequest( rpcModule, "personal_newAccount", passphrase);
            var accountsNow = _wallet.GetAccounts();
            Assert.AreEqual(accountsBefore + 1, accountsNow.Length, "length");
            Assert.AreEqual($"{{\"jsonrpc\":\"2.0\",\"result\":\"{accountsNow.Last()}\",\"id\":67}}", serialized);
        }
        
        [Test]
        [Ignore("Cannot reproduce GO signing yet")]
        public void Personal_ec_sign()
        {
            IPersonalRpcModule rpcModule = new PersonalRpcModule(_ecdsa, _wallet, _keyStore);
            string serialized = RpcTest.TestSerializedRequest(rpcModule, "personal_sign", "0xdeadbeaf", "0x9b2055d370f73ec7d8a03e965129118dc8f5bf83");
            Assert.AreEqual($"{{\"jsonrpc\":\"2.0\",\"result\":\"0xa3f20717a250c2b0b729b7e5becbff67fdaef7e0699da4de7ca5895b02a170a12d887fd3b17bfdce3481f10bea41f45ba9f709d39ce8325427b57afcfc994cee1b\"}}", serialized);
        }
        
        [Test]
        [Ignore("Cannot reproduce GO signing yet")]
        public void Personal_ec_recover()
        {
            IPersonalRpcModule rpcModule = new PersonalRpcModule(_ecdsa, _wallet, _keyStore);
            string serialized = RpcTest.TestSerializedRequest(rpcModule, "personal_ecRecover", "0xdeadbeaf", "0xa3f20717a250c2b0b729b7e5becbff67fdaef7e0699da4de7ca5895b02a170a12d887fd3b17bfdce3481f10bea41f45ba9f709d39ce8325427b57afcfc994cee1b");
            Assert.AreEqual($"{{\"jsonrpc\":\"2.0\",\"result\":\"0x9b2055d370f73ec7d8a03e965129118dc8f5bf83\"}}", serialized);
        }
    }
}
