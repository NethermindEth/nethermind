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

using System;
using System.Collections.Generic;
using Nethermind.Logging;
using Nethermind.JsonRpc.Modules;
using NUnit.Framework;
using NSubstitute;

namespace Nethermind.JsonRpc.Test
{
    [TestFixture]
    public class JsonRpcUrlCollectionTests
    {
        [SetUp]
        public void Initialize()
        {
            _enabledModules = new [] { ModuleType.Eth, ModuleType.Web3, ModuleType.Net };
        }

        private string[] _enabledModules;

        [TearDown]
        public void TearDown()
        {
            Environment.SetEnvironmentVariable("NETHERMIND_URL", null, EnvironmentVariableTarget.Process);
        }

        [Test]
        public void Empty_when_disabled()
        {
            JsonRpcConfig jsonRpcConfig = new JsonRpcConfig() { Enabled = false };
            JsonRpcUrlCollection urlCollection = new JsonRpcUrlCollection(Substitute.For<ILogManager>(), jsonRpcConfig, true);
            CollectionAssert.IsEmpty(urlCollection);
        }

        [Test]
        public void Contains_single_default_url()
        {
            JsonRpcConfig jsonRpcConfig = new JsonRpcConfig()
            {
                Enabled = true,
                EnabledModules = _enabledModules
            };

            JsonRpcUrlCollection urlCollection = new JsonRpcUrlCollection(Substitute.For<ILogManager>(), jsonRpcConfig, true);
            CollectionAssert.AreEquivalent(new Dictionary<int, JsonRpcUrl>()
            {
                { 8545, new JsonRpcUrl("http", "127.0.0.1", 8545, RpcEndpoint.Http | RpcEndpoint.Ws, false, _enabledModules) }
            }, urlCollection);
        }

        [Test]
        public void Contains_single_default_url_overridden_by_environment_variable()
        {
            Environment.SetEnvironmentVariable("NETHERMIND_URL", "http://localhost:1234", EnvironmentVariableTarget.Process);

            JsonRpcConfig jsonRpcConfig = new JsonRpcConfig()
            {
                Enabled = true,
                EnabledModules = _enabledModules
            };

            JsonRpcUrlCollection urlCollection = new JsonRpcUrlCollection(Substitute.For<ILogManager>(), jsonRpcConfig, true);
            CollectionAssert.AreEquivalent(new Dictionary<int, JsonRpcUrl>()
            {
                { 1234, new JsonRpcUrl("http", "localhost", 1234, RpcEndpoint.Http | RpcEndpoint.Ws, false, _enabledModules) }
            }, urlCollection);
        }

        [Test]
        public void Contains_multiple_default_urls_with_different_ws_port()
        {
            JsonRpcConfig jsonRpcConfig = new JsonRpcConfig()
            {
                Enabled = true,
                WebSocketsPort = 1234,
                EnabledModules = _enabledModules
            };

            JsonRpcUrlCollection urlCollection = new JsonRpcUrlCollection(Substitute.For<ILogManager>(), jsonRpcConfig, true);
            CollectionAssert.AreEquivalent(new Dictionary<int, JsonRpcUrl>()
            {
                { 8545, new JsonRpcUrl("http", "127.0.0.1", 8545, RpcEndpoint.Http, false, _enabledModules) },
                { 1234, new JsonRpcUrl("http", "127.0.0.1", 1234, RpcEndpoint.Ws, false, _enabledModules) }
            }, urlCollection);
        }

        [Test]
        public void Contains_single_default_http_url_when_ws_disabled()
        {
            JsonRpcConfig jsonRpcConfig = new JsonRpcConfig()
            {
                Enabled = true,
                WebSocketsPort = 1234,
                EnabledModules = _enabledModules
            };

            JsonRpcUrlCollection urlCollection = new JsonRpcUrlCollection(Substitute.For<ILogManager>(), jsonRpcConfig, false);
            CollectionAssert.AreEquivalent(new Dictionary<int, JsonRpcUrl>()
            {
                { 8545, new JsonRpcUrl("http", "127.0.0.1", 8545, RpcEndpoint.Http, false,_enabledModules) },
            }, urlCollection);
        }

        [Test]
        public void Contains_additional_urls()
        {
            JsonRpcConfig jsonRpcConfig = new JsonRpcConfig()
            {
                Enabled = true,
                EnabledModules = _enabledModules,
                AdditionalRpcUrls = new [] { "https://localhost:1234|https;wss|admin;debug" }
            };

            JsonRpcUrlCollection urlCollection = new JsonRpcUrlCollection(Substitute.For<ILogManager>(), jsonRpcConfig, true);
            CollectionAssert.AreEquivalent(new Dictionary<int, JsonRpcUrl>()
            {
                { 8545, new JsonRpcUrl("http", "127.0.0.1", 8545, RpcEndpoint.Http | RpcEndpoint.Ws, false, _enabledModules) },
                { 1234, new JsonRpcUrl("https", "localhost", 1234, RpcEndpoint.Https | RpcEndpoint.Wss, false, new[] { "admin", "debug" }) }
            }, urlCollection);
        }

        [Test]
        public void Skips_additional_ws_only_urls_when_ws_disabled()
        {
            JsonRpcConfig jsonRpcConfig = new JsonRpcConfig()
            {
                Enabled = true,
                EnabledModules = _enabledModules,
                AdditionalRpcUrls = new [] { "http://localhost:1234|ws|admin;debug" }
            };

            JsonRpcUrlCollection urlCollection = new JsonRpcUrlCollection(Substitute.For<ILogManager>(), jsonRpcConfig, false);
            CollectionAssert.AreEquivalent(new Dictionary<int, JsonRpcUrl>()
            {
                { 8545, new JsonRpcUrl("http", "127.0.0.1", 8545,  RpcEndpoint.Http, false, _enabledModules) }
            }, urlCollection);
        }

        [Test]
        public void Clears_flag_on_additional_ws_urls_when_ws_disabled()
        {
            JsonRpcConfig jsonRpcConfig = new JsonRpcConfig()
            {
                Enabled = true,
                EnabledModules = _enabledModules,
                AdditionalRpcUrls = new [] { "http://localhost:1234|http;ws|admin;debug" }
            };

            JsonRpcUrlCollection urlCollection = new JsonRpcUrlCollection(Substitute.For<ILogManager>(), jsonRpcConfig, false);
            CollectionAssert.AreEquivalent(new Dictionary<int, JsonRpcUrl>()
            {
                { 8545, new JsonRpcUrl("http", "127.0.0.1", 8545, RpcEndpoint.Http, false, _enabledModules) },
                { 1234, new JsonRpcUrl("http", "localhost", 1234, RpcEndpoint.Http,  false, new [] { "admin", "debug" }) }
            }, urlCollection);
        }

        [Test]
        public void Skips_additional_urls_with_port_conficts()
        {
            JsonRpcConfig jsonRpcConfig = new JsonRpcConfig()
            {
                Enabled = true,
                EnabledModules = _enabledModules,
                WebSocketsPort = 9876,
                AdditionalRpcUrls = new []
                {
                    "http://localhost:8545|http;ws|admin;debug",
                    "https://127.0.0.1:1234|https;wss|eth;web3",
                    "https://127.0.0.1:9876|https;wss|net;proof",
                    "http://localhost:1234|http;ws|db;erc20"
                }
            };

            JsonRpcUrlCollection urlCollection = new JsonRpcUrlCollection(Substitute.For<ILogManager>(), jsonRpcConfig, true);
            CollectionAssert.AreEquivalent(new Dictionary<int, JsonRpcUrl>()
            {
                { 8545, new JsonRpcUrl("http", "127.0.0.1", 8545, RpcEndpoint.Http, false, _enabledModules) },
                { 9876, new JsonRpcUrl("http", "127.0.0.1", 9876, RpcEndpoint.Ws,false, _enabledModules) },
                { 1234, new JsonRpcUrl("https", "127.0.0.1", 1234, RpcEndpoint.Https | RpcEndpoint.Wss, false, new [] { "eth", "web3" }) }
            }, urlCollection);
        }

        [Test]
        public void Skips_additional_urls_when_invalid()
        {
            JsonRpcConfig jsonRpcConfig = new JsonRpcConfig()
            {
                Enabled = true,
                EnabledModules = _enabledModules,
                AdditionalRpcUrls = new []
                {
                    string.Empty,
                    "test",
                    "http://localhost:1234|http|db;erc20;web3"
                }
            };

            JsonRpcUrlCollection urlCollection = new JsonRpcUrlCollection(Substitute.For<ILogManager>(), jsonRpcConfig, true);
            CollectionAssert.AreEquivalent(new Dictionary<int, JsonRpcUrl>()
            {
                { 8545, new JsonRpcUrl("http", "127.0.0.1", 8545, RpcEndpoint.Http | RpcEndpoint.Ws, false, _enabledModules) },
                { 1234, new JsonRpcUrl("http", "localhost", 1234, RpcEndpoint.Http, false, new [] { "db", "erc20", "web3" }) }
            }, urlCollection); ;
        }
    }
}
