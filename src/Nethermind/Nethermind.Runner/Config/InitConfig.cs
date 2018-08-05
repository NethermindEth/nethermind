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

using Nethermind.Core;

namespace Nethermind.Runner.Config
{
    public class InitConfig : IInitConfig
    {
        public bool TransactionTracingEnabled { get; set; } = false;
        public string BaseTracingPath { get; set; } = "traces";
        public bool JsonRpcEnabled { get; set; } = true;
        public bool DiscoveryEnabled { get; set; } = true;
        public bool SynchronizationEnabled { get; set; } = true;
        public bool NetworkEnabled { get; set; } = true;
        public bool IsMining { get; set; } = false;
        public int FakeMiningDelay { get; set; } = 12000;
        public string HttpHost { get; set; } = "127.0.0.1";
        public int HttpPort { get; set; } = 8345;
        public int DiscoveryPort { get; set; } = 30312;
        public int P2PPort { get; set; } = 30312;
        public string ChainSpecPath { get; set; }
        public string BaseDbPath { get; set; } = "db";
        public string TestNodeKey { get; set; } = "020102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f";
        public string LogFileName { get; set; } = "log.txt";
        public string GenesisHash { get; set; }
        public string[] JsonRpcEnabledModules { get; set; } = { "Eth", "Net", "Web3", "Db", "Shh" };
        public bool RemovingLogFilesEnabled { get; set; } = true;
    }
}