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

using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Net;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules
{
    [TestFixture]
    public class RpcModuleProviderTests
    {
        private RpcModuleProvider _moduleProvider;

        [SetUp]
        public void Initialize()
        {
            _moduleProvider = new RpcModuleProvider(new JsonRpcConfig(), LimboLogs.Instance);
        }

        [Test]
        public void Method_resolution_is_not_case_sensitive()
        {
            SingletonModulePool<INetModule> pool = new SingletonModulePool<INetModule>(new NetModule(LimboLogs.Instance, Substitute.For<INetBridge>()), true);
            _moduleProvider.Register(pool);

            ModuleResolution resolution = _moduleProvider.Check("net_VeRsIoN");
            Assert.AreEqual(ModuleResolution.Enabled, resolution);
        }
        
        [Test]
        public void Returns_politely_when_no_method_found()
        {
            SingletonModulePool<INetModule> pool = new SingletonModulePool<INetModule>(new NetModule(LimboLogs.Instance, Substitute.For<INetBridge>()), true);
            _moduleProvider.Register(pool);

            ModuleResolution resolution = _moduleProvider.Check("unknown_method");
            Assert.AreEqual(ModuleResolution.Unknown, resolution);
        }
    }
}