//  Copyright (c) 2018 Demerzel Solutions Limited
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