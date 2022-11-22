// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace Nethermind.Peering.Mothra
{
    public class MothraSettings
    {
        public IList<string> BootNodes { get; } = new List<string>();

        public string? DataDirectory { get; set; }

        public LogLevel? DebugLevel { get; set; }

        public string? DiscoveryAddress { get; set; }

        public int? DiscoveryPort { get; set; }

        public string? ListenAddress { get; set; }

        public int? MaximumPeers { get; set; }

        public IList<string> PeerMultiAddresses { get; } = new List<string>();

        public int? Port { get; set; }

        public IList<string> Topics { get; } = new List<string>();

        public int? VerbosityLevel { get; set; }
    }
}
