// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.BeaconNode.Peering
{
    public class MothraConfiguration
    {
        public string[] BootNodes { get; set; } = new string[0];

        public string? DiscoveryAddress { get; set; }

        public int? DiscoveryPort { get; set; }

        public string? ListenAddress { get; set; }

        public bool LogSignedBeaconBlockJson { get; set; }

        public int? MaximumPeers { get; set; }

        public int? Port { get; set; }
    }
}
