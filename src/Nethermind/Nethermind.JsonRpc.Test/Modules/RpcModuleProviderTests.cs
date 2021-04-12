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

using System.IO.Abstractions;
using FluentAssertions;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Net;
using Nethermind.JsonRpc.Modules.Proof;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class RpcModuleProviderTests
    {
        private RpcModuleProvider _moduleProvider;

        private IFileSystem _fileSystem;

        [SetUp]
        public void Initialize()
        {
            _fileSystem = Substitute.For<IFileSystem>();
            _moduleProvider = new RpcModuleProvider(_fileSystem, new JsonRpcConfig(), LimboLogs.Instance);
        }

        [Test]
        public void Module_provider_will_recognize_disabled_modules()
        {
            JsonRpcConfig jsonRpcConfig = new JsonRpcConfig();
            jsonRpcConfig.EnabledModules = new string[0];
            _moduleProvider = new RpcModuleProvider(new FileSystem(), jsonRpcConfig, LimboLogs.Instance);
            _moduleProvider.Register(new SingletonModulePool<IProofRpcModule>(Substitute.For<IProofRpcModule>(), false));
            ModuleResolution resolution = _moduleProvider.Check("proof_call", RpcEndpoint.Http);
            Assert.AreEqual(ModuleResolution.Disabled, resolution);
        }

        [Test]
        public void Method_resolution_is_case_sensitive()
        {
            SingletonModulePool<INetRpcModule> pool = new SingletonModulePool<INetRpcModule>(new NetRpcModule(LimboLogs.Instance, Substitute.For<INetBridge>()), true);
            _moduleProvider.Register(pool);

            _moduleProvider.Check("net_VeRsIoN", RpcEndpoint.Http).Should().Be(ModuleResolution.Unknown);
            _moduleProvider.Check("net_Version", RpcEndpoint.Http).Should().Be(ModuleResolution.Unknown);
            _moduleProvider.Check("Net_Version", RpcEndpoint.Http).Should().Be(ModuleResolution.Unknown);
            _moduleProvider.Check("net_version", RpcEndpoint.Http).Should().Be(ModuleResolution.Enabled);
        }

        [TestCase("eth_.*", ModuleResolution.Unknown)]
        [TestCase("net_.*", ModuleResolution.Enabled)]
        public void With_filter_can_reject(string regex, ModuleResolution expectedResult)
        {
            JsonRpcConfig config = new JsonRpcConfig();
            _fileSystem.File.Exists(Arg.Any<string>()).Returns(true);
            _fileSystem.File.ReadLines(Arg.Any<string>()).Returns(new[] {regex});
            _moduleProvider = new RpcModuleProvider(_fileSystem, config, LimboLogs.Instance);
            
            SingletonModulePool<INetRpcModule> pool = new SingletonModulePool<INetRpcModule>(new NetRpcModule(LimboLogs.Instance, Substitute.For<INetBridge>()), true);
            _moduleProvider.Register(pool);

            ModuleResolution resolution = _moduleProvider.Check("net_version", RpcEndpoint.Http);
            resolution.Should().Be(expectedResult);
        }

        [Test]
        public void Returns_politely_when_no_method_found()
        {
            SingletonModulePool<INetRpcModule> pool = new SingletonModulePool<INetRpcModule>(Substitute.For<INetRpcModule>(), true);
            _moduleProvider.Register(pool);

            ModuleResolution resolution = _moduleProvider.Check("unknown_method", RpcEndpoint.Http);
            Assert.AreEqual(ModuleResolution.Unknown, resolution);
        }
    }
}
