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

namespace Nethermind.Cli.Modules
{
    [CliModule("clique")]
    public class CliqueCliModule : CliModuleBase
    {
        [CliFunction("clique", "getSnapshot")]
        public string GetSnapshot()
        {
            return NodeManager.Post<string>("clique_getSnapshot").Result;
        }

        [CliFunction("clique", "getSnapshotAtHash")]
        public string GetSnapshotAtHash(string hash)
        {
            return NodeManager.Post<string>("clique_getSnapshotAtHash", hash).Result;
        }
        
        [CliFunction("clique", "getSigners")]
        public string[] GetSigners()
        {
            return NodeManager.Post<string[]>("clique_gitSigners").Result;
        }
        
        [CliFunction("clique", "getSignersAtHash")]
        public string[] GetSignersAtHash()
        {
            return NodeManager.Post<string[]>("clique_gitSignersAtHash").Result;
        }
        
        [CliFunction("clique", "propose")]
        public bool Propose(string address, bool vote)
        {
            return NodeManager.Post<bool>("clique_propose", address, vote).Result;
        }
        
        [CliFunction("clique", "discard")]
        public bool Propose(string address)
        {
            return NodeManager.Post<bool>("clique_discard", address).Result;
        }

        public CliqueCliModule(ICliEngine cliEngine, INodeManager nodeManager) : base(cliEngine, nodeManager)
        {
        }
    }
}