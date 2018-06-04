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

using System.Numerics;
using Nethermind.Runner.Runners;

namespace Nethermind.Runner
{
    // TODO: remove this, use separate config files for hive and for runner - at the moment we just throw everything at this file
    public class InitParams
    {
        public bool TransactionTracingEnabled { get; set; }
        public string BaseTracingPath { get; set; }
        public bool JsonRpcEnabled { get; set; } = true;
        public bool DiscoveryEnabled { get; set; } = true;
        public bool SynchronizationEnabled { get; set; } = true;
        public bool? IsMining { get; set; }
        public int? FakeMiningDelay { get; set; }
        public string HttpHost { get; set; }
        public string Bootnode { get; set; }
        public int? HttpPort { get; set; }
        public int? DiscoveryPort { get; set; }
        public int? P2PPort { get; set; }
        public string ChainSpecPath { get; set; }
        public string BaseDbPath { get; set; }
        public string GenesisFilePath { get; set; }
        public string ChainFile { get; set; }
        public string BlocksDir { get; set; }
        public string KeysDir { get; set; }
        public string TestNodeKey { get; set; }
        public string LogFileName { get; set; }
        public string ExpectedGenesisHash { get; set; }
        public BigInteger? HomesteadBlockNr { get; set; }
        public EthereumRunnerType EthereumRunnerType { get; set; }
        public string[] JsonRpcEnabledModules { get; set; }

        public override string ToString()
        {
            return $"HttpHost: {HttpHost}, Bootnode: {Bootnode}, HttpPort: {HttpPort}, DiscoveryPort: {DiscoveryPort}, GenesisFilePath: {GenesisFilePath}, " +
                   $"ChainFile: {ChainFile}, BlocksDir: {BlocksDir}, KeysDir: {KeysDir}, HomesteadBlockNr: {HomesteadBlockNr}, EthereumRunnerType: {EthereumRunnerType}";
        }
    }
}