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

using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Facade;
using Nethermind.JsonRpc.Modules.Personal;
using Nethermind.Logging;
using Nethermind.Wallet;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules
{
    [TestFixture]
    public class PersonalModuleTests
    {
        [SetUp]
        public void Initialize()
        {
            _wallet = new DevWallet(new WalletConfig(),  LimboLogs.Instance);
            IEthereumEcdsa ethereumEcdsa = new EthereumEcdsa(MainNetSpecProvider.Instance, LimboLogs.Instance);
            _bridge = new PersonalBridge(ethereumEcdsa, _wallet);
        }

        private IPersonalBridge _bridge;
        private DevWallet _wallet;

        [Test]
        public void Personal_list_accounts()
        {
            IPersonalModule module = new PersonalModule(_bridge, NullLogManager.Instance);
            string serialized = RpcTest.TestSerializedRequest(module, "personal_listAccounts");
            string expectedAccounts = string.Join(',', _bridge.ListAccounts().Select(a => $"\"{a.ToString()}\""));
            Assert.AreEqual($"{{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":[{expectedAccounts}]}}", serialized);
        }
        
        [Test]
        public void Personal_new_account()
        {
            int accountsBefore = _bridge.ListAccounts().Length;
            string passphrase = "testPass";
            IPersonalModule module = new PersonalModule(_bridge, NullLogManager.Instance);
            string serialized = RpcTest.TestSerializedRequest( module, "personal_newAccount", passphrase);
            var accountsNow = _bridge.ListAccounts();
            Assert.AreEqual(accountsBefore + 1, accountsNow.Length, "length");
            Assert.AreEqual($"{{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":\"{accountsNow.Last()}\"}}", serialized);
        }
        
        [Test]
        [Ignore("Cannot reproduce GO signing yet")]
        public void Personal_ec_sign()
        {
            IPersonalModule module = new PersonalModule(_bridge, NullLogManager.Instance);
            string serialized = RpcTest.TestSerializedRequest(module, "personal_sign", "0xdeadbeaf", "0x9b2055d370f73ec7d8a03e965129118dc8f5bf83");
            Assert.AreEqual($"{{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":\"0xa3f20717a250c2b0b729b7e5becbff67fdaef7e0699da4de7ca5895b02a170a12d887fd3b17bfdce3481f10bea41f45ba9f709d39ce8325427b57afcfc994cee1b\"}}", serialized);
        }
        
        [Test]
        [Ignore("Cannot reproduce GO signing yet")]
        public void Personal_ec_recover()
        {
            IPersonalModule module = new PersonalModule(_bridge, NullLogManager.Instance);
            string serialized = RpcTest.TestSerializedRequest(module, "personal_ecRecover", "0xdeadbeaf", "0xa3f20717a250c2b0b729b7e5becbff67fdaef7e0699da4de7ca5895b02a170a12d887fd3b17bfdce3481f10bea41f45ba9f709d39ce8325427b57afcfc994cee1b");
            Assert.AreEqual($"{{\"id\":67,\"jsonrpc\":\"2.0\",\"result\":\"0x9b2055d370f73ec7d8a03e965129118dc8f5bf83\"}}", serialized);
        }
    }
}