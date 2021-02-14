//  Copyright (c) 2021 Demerzel Solutions Limited
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

using System;
using System.IO;
using Jint.Native;
using Nethermind.Config;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;

namespace Nethermind.Cli.Modules
{
    [CliModule("node")]
    public class NodeCliModule : CliModuleBase
    {
        [CliFunction("node", "setNodeKey")]
        public string SetNodeKey(string key)
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory!, "node.key.plain");
            File.WriteAllBytes("node.key.plain", new PrivateKey(Bytes.FromHexString(key)).KeyBytes);
            return path;
        }

        [CliFunction("node", "switch")]
        public string Switch(string uri)
        {
            if (!uri.Contains(":"))
            {
                uri = uri + ":8545";
            }
            
            if (!uri.StartsWith("http://") && !uri.StartsWith("https://" ))
            {
                uri = "http://" + uri;
            }
            
            NodeManager.SwitchUri(new Uri($"{uri}"));
            return uri;
        }
        
        [CliFunction("node", "switchLocal")]
        public string SwitchLocal(string uri)
        {
            uri = $"{GetVariable("NETHERMIND_CLI_SWITCH_LOCAL", "http://localhost")}:{uri}";
            NodeManager.SwitchUri(new Uri(uri));
            return uri;
        }
        
        private static string? GetVariable(string name, string defaultValue)
        {
            string? value = Environment.GetEnvironmentVariable(name.ToUpperInvariant());
            return string.IsNullOrWhiteSpace(value) ? value : defaultValue;
        }
        
        [CliProperty("node", "address")]
        public string Address()
        {
            return new Enode(Enode()).Address.ToString();
        }
        
        [CliProperty("node", "enode")]
        public string? Enode()
        {
            return NodeManager.Post<string>("net_localEnode").Result;
        }
        
        [CliProperty("node", "uri")]
        public JsValue Uri()
        {
            return NodeManager.CurrentUri;
        }

        public NodeCliModule(ICliEngine cliEngine, INodeManager nodeManager) : base(cliEngine, nodeManager)
        {
        }
    }
}
