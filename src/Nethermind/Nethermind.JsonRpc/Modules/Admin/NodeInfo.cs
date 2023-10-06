// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Modules.Admin
{
    /// <summary>
    /// {
    ///     enode: "enode://44826a5d6a55f88a18298bca4773fca5749cdc3a5c9f308aa7d810e9b31123f3e7c5fba0b1d70aac5308426f47df2a128a6747040a3815cc7dd7167d03be320d@[::]:30303",
    ///     id: "44826a5d6a55f88a18298bca4773fca5749cdc3a5c9f308aa7d810e9b31123f3e7c5fba0b1d70aac5308426f47df2a128a6747040a3815cc7dd7167d03be320d",
    ///     ip: "::",
    ///     listenAddr: "[::]:30303",
    ///     name: "Geth/v1.5.0-unstable/linux/go1.6",
    ///     ports: {
    ///     discovery: 30303,
    ///     listener: 30303
    ///     },
    ///     protocols: {
    ///     eth: {
    ///         difficulty: 17334254859343145000,
    ///         genesis: "0xd4e56740f876aef8c010b86a40d5f56745a118d0906a34e69aec8c0db1cb8fa3",
    ///         head: "0xb83f73fbe6220c111136aefd27b160bf4a34085c65ba89f24246b3162257c36a",
    ///         network: 1
    ///     }
    ///     }
    /// }
    /// </summary>
    public class NodeInfo
    {
        public NodeInfo()
        {
            Protocols = new Dictionary<string, EthProtocolInfo>
            {
                { "eth", new EthProtocolInfo() }
            };
            Ports = new PortsInfo();
        }

        [JsonProperty("enode", Order = 0)]
        public string Enode { get; set; }

        [JsonProperty("id", Order = 1)]
        public string Id { get; set; }

        [JsonProperty("ip", Order = 2)]
        public string? Ip { get; set; }

        [JsonProperty("listenAddr", Order = 3)]
        public string ListenAddress { get; set; }

        [JsonProperty("name", Order = 4)]
        public string Name { get; set; }

        [JsonProperty("ports", Order = 5)]
        public PortsInfo Ports { get; set; }

        [JsonProperty("protocols", Order = 6)]
        public Dictionary<string, EthProtocolInfo> Protocols { get; set; }
    }
}
