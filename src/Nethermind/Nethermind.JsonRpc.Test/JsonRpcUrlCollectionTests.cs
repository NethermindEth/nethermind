// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
            _enabledModules = new[] { ModuleType.Eth, ModuleType.Web3, ModuleType.Net };
        }

        private string[] _enabledModules = null!;

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
                AdditionalRpcUrls = new[] { "https://localhost:1234|https;wss|admin;debug" }
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
                AdditionalRpcUrls = new[] { "http://localhost:1234|ws|admin;debug" }
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
                AdditionalRpcUrls = new[] { "http://localhost:1234|http;ws|admin;debug" }
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
                AdditionalRpcUrls = new[]
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
                AdditionalRpcUrls = new[]
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

        [Test]
        public void EngineHost_and_EnginePort_specified()
        {
            JsonRpcConfig jsonRpcConfig = new JsonRpcConfig()
            {
                Enabled = true,
                EnabledModules = _enabledModules,
                AdditionalRpcUrls = new[]
                {
                    "http://127.0.0.1:8551|http|eth;web3;engine"
                },
                EngineHost = "127.0.0.1",
                EnginePort = 8551,
                EngineEnabledModules = new[] { "eth" }
            };

            JsonRpcUrlCollection urlCollection = new JsonRpcUrlCollection(Substitute.For<ILogManager>(), jsonRpcConfig, true);
            CollectionAssert.AreEquivalent(new Dictionary<int, JsonRpcUrl>()
            {
                { 8545, new JsonRpcUrl("http", "127.0.0.1", 8545, RpcEndpoint.Http | RpcEndpoint.Ws, false, _enabledModules) },
                { 8551, new JsonRpcUrl("http", "127.0.0.1", 8551, RpcEndpoint.Http | RpcEndpoint.Ws, true, new [] { "eth", "engine" }) },
            }, urlCollection); ;
        }

        [Test]
        public void Skips_AdditionalUrl_with_engine_module_enabled_when_EngineUrl_specified()
        {
            JsonRpcConfig jsonRpcConfig = new JsonRpcConfig()
            {
                Enabled = true,
                EnabledModules = _enabledModules,
                AdditionalRpcUrls = new[]
                {
                    "http://127.0.0.1:8551|http|eth;web3;engine",
                    "http://127.0.0.1:1234|http|eth;web3"
                },
                EngineHost = "127.0.0.1",
                EnginePort = 8552,
                EngineEnabledModules = new[] { "eth" }
            };

            JsonRpcUrlCollection urlCollection = new JsonRpcUrlCollection(Substitute.For<ILogManager>(), jsonRpcConfig, false);
            CollectionAssert.AreEquivalent(new Dictionary<int, JsonRpcUrl>()
            {
                { 8545, new JsonRpcUrl("http", "127.0.0.1", 8545, RpcEndpoint.Http, false, _enabledModules) },
                { 8552, new JsonRpcUrl("http", "127.0.0.1", 8552, RpcEndpoint.Http, true, new [] { "eth", "engine" }) },
                { 1234, new JsonRpcUrl("http", "127.0.0.1", 1234, RpcEndpoint.Http, false, new [] { "eth", "web3" })}
            }, urlCollection); ;
        }
    }
}
