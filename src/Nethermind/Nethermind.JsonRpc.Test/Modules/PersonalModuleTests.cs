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
using Nethermind.Config;
using Nethermind.Core.Json;
using Nethermind.Facade;
using Nethermind.JsonRpc.Modules.Personal;
using Nethermind.Logging;
using Nethermind.Wallet;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules
{
    [TestFixture]
    public class PersonalModuleTests
    {
        [SetUp]
        public void Initialize()
        {
            _wallet = new DevWallet(LimboLogs.Instance);
            _bridge = new PersonalBridge(_wallet);
        }

        private IPersonalBridge _bridge;
        private IWallet _wallet;

        [Test]
        public void Personal_list_accounts()
        {
            IPersonalModule module = new PersonalModule(_bridge, NullLogManager.Instance);
            string serialized = RpcTest.TestSerializedRequest(module, "personal_listAccounts");
            string expectedAccounts = string.Join(',', _bridge.ListAccounts().Select(a => $"\"{a.ToString()}\""));
            Assert.AreEqual($"{{\"id\":\"0x43\",\"jsonrpc\":\"2.0\",\"result\":[{expectedAccounts}]}}", serialized);
        }
        
        [Test]
        public void Personal_new_account()
        {
            int accountsBefore = _bridge.ListAccounts().Length;
            string passphrase = "testPass";
            IPersonalModule module = new PersonalModule(_bridge, NullLogManager.Instance);
            string serialized = RpcTest.TestSerializedRequest(module, "personal_newAccount", passphrase);
            var accountsNow = _bridge.ListAccounts();
            Assert.AreEqual(accountsBefore + 1, accountsNow.Length, "length");
            Assert.AreEqual($"{{\"id\":\"0x43\",\"jsonrpc\":\"2.0\",\"result\":\"{accountsNow.Last()}\"}}", serialized);
        }
    }
}