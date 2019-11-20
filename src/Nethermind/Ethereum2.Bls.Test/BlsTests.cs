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

using System.IO;
using System.Linq;
using NUnit.Framework;
using YamlDotNet.RepresentationModel;

namespace Ethereum2.Bls.Test
{
    public class BlsTests
    {
        [Test]
        public void Bls_aggregate_pubkeys()
        {
            string[] valid = Directory.GetDirectories(Path.Combine("aggregate_pubkeys", "small"));
            (YamlNode node, YamlNodeType nodeType) = LoadValue(Path.Combine(valid[0], "data.yaml"));
            string[] inputHex = node.ArrayProp<string>("input");
            string outputHex = node.Prop<string>("output");
        }

        [Test]
        public void Bls_aggregate_sigs()
        {
            string[] valid = Directory.GetDirectories(Path.Combine("aggregate_sigs", "small"));
            (YamlNode node, YamlNodeType nodeType) = LoadValue(Path.Combine(valid[0], "data.yaml"));
            string[] inputHex = node.ArrayProp<string>("input");
            string outputHex = node.Prop<string>("output");
        }

        [Test]
        public void Bls_msg_hash_compressed()
        {
            string[] valid = Directory.GetDirectories(Path.Combine("msg_hash_compressed", "small"));
            (YamlNode node, YamlNodeType nodeType) = LoadValue(Path.Combine(valid[0], "data.yaml"));
            var input = new {Message = node["input"].Prop<string>("message"), Domain = node["input"].Prop<string>("domain")};
            string[] outputHex = node.ArrayProp<string>("output");
        }

        [Test]
        public void Bls_msg_hash_uncompressed()
        {
            string[] valid = Directory.GetDirectories(Path.Combine("msg_hash_uncompressed", "small"));
            (YamlNode node, YamlNodeType nodeType) = LoadValue(Path.Combine(valid[0], "data.yaml"));
            var input = new {Message = node["input"].Prop<string>("message"), Domain = node["input"].Prop<string>("domain")};
            string[][] outputHex = node.ArrayProp<string[]>("output", sequence => sequence.Children.Select(c => (c as YamlScalarNode).Value).ToArray());
        }

        [Test]
        public void Bls_priv_to_pub()
        {
            string[] valid = Directory.GetDirectories(Path.Combine("priv_to_pub", "small"));
            (YamlNode node, YamlNodeType nodeType) = LoadValue(Path.Combine(valid[0], "data.yaml"));
            string inputHex = node.Prop<string>("input");
            string outputHex = node.Prop<string>("output");
        }

        [Test]
        public void Bls_sign_msg()
        {
            string[] valid = Directory.GetDirectories(Path.Combine("sign_msg", "small"));
            (YamlNode node, YamlNodeType nodeType) = LoadValue(Path.Combine(valid[0], "data.yaml"));
            var input = new {PrivateKey = node["input"].Prop<string>("privkey"), Message = node["input"].Prop<string>("message"), Domain = node["input"].Prop<string>("domain")};
            string outputHex = node.Prop<string>("output");
        }

        private static (YamlNode rootNode, YamlNodeType nodeType) LoadValue(string file)
        {
            using FileStream fileStream = File.OpenRead(file); // value.yaml
            using var input = new StreamReader(fileStream);
            var yaml = new YamlStream();
            yaml.Load(input);

            var rootNode = yaml.Documents[0].RootNode;
            YamlNodeType nodeType = rootNode.NodeType;
            return (rootNode, nodeType);
        }
    }
}