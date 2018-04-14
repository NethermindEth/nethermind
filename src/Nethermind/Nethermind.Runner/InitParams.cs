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
    public class InitParams
    {
        public string HttpHost { get; set; }
        public string BootNode { get; set; }
        public int? HttpPort { get; set; }
        public int? DiscoveryPort { get; set; }
        public string GenesisFilePath { get; set; }
        public string ChainFile { get; set; }
        public string BlocksDir { get; set; }
        public string KeysDir { get; set; }
        public BigInteger? HomesteadBlockNr { get; set; }
        public EthereumRunnerType EthereumRunnerType { get; set; }

        public override string ToString()
        {
            return $"HttpHost: {HttpHost}, BootNode: {BootNode}, HttpPort: {HttpPort}, DiscoveryPort: {DiscoveryPort}, GenesisFilePath: {GenesisFilePath}, " +
                   $"ChainFile: {ChainFile}, BlocksDir: {BlocksDir}, KeysDir: {KeysDir}, HomesteadBlockNr: {HomesteadBlockNr}, EthereumRunnerType: {EthereumRunnerType}";
        }
    }
}