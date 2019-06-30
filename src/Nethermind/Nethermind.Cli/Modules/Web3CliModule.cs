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

using System.Globalization;
using Jint.Native;

namespace Nethermind.Cli.Modules
{
    [CliModule("web3")]
    public class Web3CliModule : CliModuleBase
    {
        public Web3CliModule(ICliEngine cliEngine, INodeManager nodeManager) : base(cliEngine, nodeManager)
        {
        }

        [CliProperty("web3", "clientVersion")]
        public string ClientVersion() => NodeManager.Post<string>("web3_clientVersion").Result;

        [CliFunction("web3", "sha3")]
        public string Sha3(string data) => NodeManager.Post<string>("web3_sha3", data).Result;
        
        [CliFunction("web3", "toDecimal")]
        public int ToDecimal(string hex) => int.Parse(hex.Replace("0x", string.Empty), NumberStyles.AllowHexSpecifier);
    }
}