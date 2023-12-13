// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using Nethermind.Core.Extensions;
using Nethermind.JsonRpc;
using Nethermind.Network.Config;
using NUnit.Framework;

namespace Nethermind.Config.Test
{
    [TestFixture]
    [Parallelizable(ParallelScope.All)]
    public class DefaultConfigProviderTests
    {
        [Test]
        public void Can_read_without_sources()
        {
            ConfigProvider configProvider = new();
            INetworkConfig config = configProvider.GetConfig<INetworkConfig>();
            Assert.That(config.DiscoveryPort, Is.EqualTo(30303));
        }

        public int DefaultTestProperty { get; set; } = 5;

        // [Test]
        // public void Can_read_defaults_from_registered_categories()
        // {
        //     ConfigProvider configProvider = new ConfigProvider();
        //     configProvider.RegisterCategory("Nananana", typeof(DefaultConfigProviderTests));
        //     var result = configProvider.GetRawValue("Nananana", nameof(DefaultTestProperty));
        //     Assert.AreEqual(5, result);
        // }

        [Test]
        public void Can_read_overwrites()
        {
            BitArray bitArray = new(6);
            for (int i = 0; i < 2 * 2 * 2 * 2 * 2 * 2; i++)
            {
                ConfigProvider configProvider = new();
                bitArray.Set(0, (i >> 0) % 2 == 1);
                bitArray.Set(1, (i >> 1) % 2 == 1);
                bitArray.Set(2, (i >> 2) % 2 == 1);
                bitArray.Set(3, (i >> 3) % 2 == 1);
                bitArray.Set(4, (i >> 4) % 2 == 1);
                bitArray.Set(5, (i >> 5) % 2 == 1);

                Dictionary<string, string> args = new();
                if (bitArray.Get(4))
                {
                    args.Add("JsonRpc.Enabled", bitArray.Get(5).ToString());
                }

                Environment.SetEnvironmentVariable("NETHERMIND_JSONRPCCONFIG_ENABLED", null, EnvironmentVariableTarget.Process);
                if (bitArray.Get(2))
                {
                    Environment.SetEnvironmentVariable("NETHERMIND_JSONRPCCONFIG_ENABLED", bitArray.Get(3).ToString(), EnvironmentVariableTarget.Process);
                }

                Dictionary<string, string> fakeJson = new();
                if (bitArray.Get(0))
                {
                    fakeJson.Add("JsonRpc.Enabled", bitArray.Get(1).ToString());
                }

                configProvider.AddSource(new ArgsConfigSource(args));
                configProvider.AddSource(new EnvConfigSource());
                configProvider.AddSource(new ArgsConfigSource(fakeJson));

                IJsonRpcConfig? config = configProvider.GetConfig<IJsonRpcConfig>();
                bool expectedResult = bitArray.Get(4)
                    ? bitArray.Get(5)
                    : bitArray.Get(2)
                        ? bitArray.Get(3)
                        : bitArray.Get(0)
                            ? bitArray.Get(1)
                            : false;

                Assert.That(config.Enabled, Is.EqualTo(expectedResult), bitArray.ToBitString());
            }
        }
    }
}
