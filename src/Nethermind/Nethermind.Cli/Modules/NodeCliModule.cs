// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

            if (!uri.StartsWith("http://") && !uri.StartsWith("https://"))
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
            return new Enode(Enode() ?? string.Empty).Address.ToString();
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
