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

using Nethermind.Config;

namespace Nethermind.Runner.Config
{
    public interface IInitConfig : IConfig
    {
        bool JsonRpcEnabled { get; set; }
        bool DiscoveryEnabled { get; set; }
        bool SynchronizationEnabled { get; set; }
        bool NetworkEnabled { get; set; }
        bool PeerManagerEnabled { get; set; }
        bool IsMining { get; set; }
        int FakeMiningDelay { get; set; }
        string HttpHost { get; set; }
        int HttpPort { get; set; }
        int DiscoveryPort { get; set; }
        int P2PPort { get; set; }
        string ChainSpecPath { get; set; }
        string BaseDbPath { get; set; }
        string TestNodeKey { get; set; }
        string GenesisHash { get; set; }
        string[] JsonRpcEnabledModules { get; set; }
        bool RemovingLogFilesEnabled { get; set; }
        string LogFileName { get; set; }
        string LogDirectory { get; set; }
        bool LogPerfStatsOnDebug { get; set; }
    }
}