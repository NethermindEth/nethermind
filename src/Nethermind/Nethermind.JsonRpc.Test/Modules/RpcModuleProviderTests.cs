// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
        private RpcModuleProvider _moduleProvider = null!;
        private IFileSystem _fileSystem = null!;
        private JsonRpcContext _context = null!;

        [SetUp]
        public void Initialize()
        {
            _fileSystem = Substitute.For<IFileSystem>();
            _moduleProvider = new RpcModuleProvider(_fileSystem, new JsonRpcConfig(), LimboLogs.Instance);
            _context = new JsonRpcContext(RpcEndpoint.Http);
        }

        [Test]
        public void Module_provider_will_recognize_disabled_modules()
        {
            JsonRpcConfig jsonRpcConfig = new();
            jsonRpcConfig.EnabledModules = new string[0];
            _moduleProvider = new RpcModuleProvider(new FileSystem(), jsonRpcConfig, LimboLogs.Instance);
            _moduleProvider.Register(new SingletonModulePool<IProofRpcModule>(Substitute.For<IProofRpcModule>(), false));
            ModuleResolution resolution = _moduleProvider.Check("proof_call", _context);
            Assert.That(resolution, Is.EqualTo(ModuleResolution.Disabled));
        }

        [Test]
        public void Method_resolution_is_case_sensitive()
        {
            SingletonModulePool<INetRpcModule> pool = new(new NetRpcModule(LimboLogs.Instance, Substitute.For<INetBridge>()), true);
            _moduleProvider.Register(pool);

            _moduleProvider.Check("net_VeRsIoN", _context).Should().Be(ModuleResolution.Unknown);
            _moduleProvider.Check("net_Version", _context).Should().Be(ModuleResolution.Unknown);
            _moduleProvider.Check("Net_Version", _context).Should().Be(ModuleResolution.Unknown);
            _moduleProvider.Check("net_version", _context).Should().Be(ModuleResolution.Enabled);
        }

        [TestCase("eth_.*", ModuleResolution.Unknown)]
        [TestCase("net_.*", ModuleResolution.Enabled)]
        public void With_filter_can_reject(string regex, ModuleResolution expectedResult)
        {
            JsonRpcConfig config = new();
            _fileSystem.File.Exists(Arg.Any<string>()).Returns(true);
            _fileSystem.File.ReadLines(Arg.Any<string>()).Returns(new[] { regex });
            _moduleProvider = new RpcModuleProvider(_fileSystem, config, LimboLogs.Instance);

            SingletonModulePool<INetRpcModule> pool = new(new NetRpcModule(LimboLogs.Instance, Substitute.For<INetBridge>()), true);
            _moduleProvider.Register(pool);

            ModuleResolution resolution = _moduleProvider.Check("net_version", _context);
            resolution.Should().Be(expectedResult);
        }

        [Test]
        public void Returns_politely_when_no_method_found()
        {
            SingletonModulePool<INetRpcModule> pool = new(Substitute.For<INetRpcModule>(), true);
            _moduleProvider.Register(pool);

            ModuleResolution resolution = _moduleProvider.Check("unknown_method", _context);
            Assert.That(resolution, Is.EqualTo(ModuleResolution.Unknown));
        }

        [Test]
        public void Method_resolution_is_scoped_to_url_enabled_modules()
        {
            _moduleProvider.Register(new SingletonModulePool<INetRpcModule>(Substitute.For<INetRpcModule>(), true));
            _moduleProvider.Register(new SingletonModulePool<IProofRpcModule>(Substitute.For<IProofRpcModule>(), true));

            JsonRpcUrl url = new JsonRpcUrl("http", "127.0.0.1", 8888, RpcEndpoint.Http, false, new[] { "net" });

            ModuleResolution inScopeResolution = _moduleProvider.Check("net_version", JsonRpcContext.Http(url));
            Assert.That(inScopeResolution, Is.EqualTo(ModuleResolution.Enabled));

            ModuleResolution outOfScopeResolution = _moduleProvider.Check("proof_call", JsonRpcContext.Http(url));
            Assert.That(outOfScopeResolution, Is.EqualTo(ModuleResolution.Disabled));

            ModuleResolution fallbackResolution = _moduleProvider.Check("proof_call", new JsonRpcContext(RpcEndpoint.Http));
            Assert.That(fallbackResolution, Is.EqualTo(ModuleResolution.Enabled));
        }

        [Test]
        public void Allows_to_get_modules()
        {
            SingletonModulePool<INetRpcModule> pool = new(Substitute.For<INetRpcModule>());
            _moduleProvider.Register(pool);
            _moduleProvider.GetPool(ModuleType.Net).Should().Be(pool);
        }

        [Test]
        public void Allows_to_replace_modules()
        {
            SingletonModulePool<INetRpcModule> pool = new(Substitute.For<INetRpcModule>());
            _moduleProvider.Register(pool);

            SingletonModulePool<INetRpcModule> pool2 = new(Substitute.For<INetRpcModule>());
            _moduleProvider.Register(pool2);

            _moduleProvider.GetPool(ModuleType.Net).Should().Be(pool2);
        }
    }
}
