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

namespace Nethermind.Runner.Config
{
    public class InitConfig : IInitConfig
    {
        public bool JsonRpcEnabled { get; set; } = false;
        public bool DiscoveryEnabled { get; set; } = true;
        public bool SynchronizationEnabled { get; set; } = true;
        public bool NetworkEnabled { get; set; } = true;
        public bool ProcessingEnabled { get; set; } = true;
        public bool PeerManagerEnabled { get; set; } = true;
        public bool RunAsReceiptsFiller { get; set; } = false;
        public bool IsMining { get; set; } = false;
        public string HttpHost { get; set; } = "127.0.0.1";
        public int HttpPort { get; set; } = 8545;
        public int DiscoveryPort { get; set; } = 30312;
        public int P2PPort { get; set; } = 30312;
        public string ChainSpecPath { get; set; }
        public string BaseDbPath { get; set; } = "db";
        public string TestNodeKey { get; set; }
        public string LogFileName { get; set; } = "log.txt";
        public string GenesisHash { get; set; }
        public string[] JsonRpcEnabledModules { get; set; } = { "Eth", "Net", "Web3", "Db", "Shh", "Nethm", "Debug" };
        public bool RemovingLogFilesEnabled { get; set; }
        //in case of null, the path is set to ExecutingAssembly.Location\logs
        public string LogDirectory { get; set; } = null;
        public bool LogPerfStatsOnDebug { get; set; } = false;
        public int ReceiptsFillerStart { get; set; } = 0;
        public int ReceiptsFillerEnd { get; set; } = 1;
        public int ObsoletePendingTransactionInterval { get; set; } = 15;
        public int RemovePendingTransactionInterval { get; set; } = 600;
        public int PeerNotificationThreshold { get; set; } = 20;
    }
}