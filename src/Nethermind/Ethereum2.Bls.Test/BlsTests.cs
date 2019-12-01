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

using System;
using System.IO;
using System.Linq;
using Nethermind.Bls;
using Nethermind.Core.Extensions;
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
            bool allFine = true;
            foreach (string testCase in valid)
            {
                (YamlNode node, YamlNodeType nodeType) = LoadValue(Path.Combine(testCase, "data.yaml"));
                string[] inputHex = node.ArrayProp<string>("input");
                string outputHex = node.Prop<string>("output");

                Span<byte> aggregated = Bytes.FromHexString(inputHex[0]);
                for (int i = 1; i < inputHex.Length; i++)
                {
                    byte[] next = Bytes.FromHexString(inputHex[i]);
                    BlsProxy.AddPublicKey(aggregated, next);
                }

                bool thisOneOk = string.Equals(outputHex, aggregated.ToHexString(true));
                Console.WriteLine(testCase + (thisOneOk ? " OK" : " FAIL"));
                allFine &= thisOneOk;
            }

            Assert.True(allFine);
        }

        [Test]
        public void Bls_aggregate_sigs()
        {
            string[] valid = Directory.GetDirectories(Path.Combine("aggregate_sigs", "small"));
            bool allFine = true;
            foreach (string testCase in valid)
            {
                (YamlNode node, YamlNodeType nodeType) = LoadValue(Path.Combine(testCase, "data.yaml"));
                string[] inputHex = node.ArrayProp<string>("input");
                string outputHex = node.Prop<string>("output");

                Span<byte> aggregated = Bytes.FromHexString(inputHex[0]);
                for (int i = 1; i < inputHex.Length; i++)
                {
                    byte[] next = Bytes.FromHexString(inputHex[i]);
                    BlsProxy.AddSignature(aggregated, next);
                }

                bool thisOneOk = string.Equals(outputHex, aggregated.ToHexString(true));
                Console.WriteLine(testCase + (thisOneOk ? " OK" : " FAIL"));
                allFine &= thisOneOk;
            }

            Assert.True(allFine);
        }

        [Test]
        [Ignore("need to build the latest BLS changes correctly")]
        public void Bls_msg_hash_compressed()
        {
            string[] valid = Directory.GetDirectories(Path.Combine("msg_hash_compressed", "small"));
            (YamlNode node, YamlNodeType nodeType) = LoadValue(Path.Combine(valid[0], "data.yaml"));
            var input = new {Message = node["input"].Prop<string>("message"), Domain = node["input"].Prop<string>("domain")};
            string[] outputHex = node.ArrayProp<string>("output");
            BlsProxy.HashWithDomain(out Span<byte> signatureBytes, out Span<byte> blsSignatureBytes, Bytes.FromHexString(input.Message), Bytes.FromHexString(input.Domain));
            Assert.AreEqual(string.Join(string.Empty, outputHex), signatureBytes.ToHexString());
        }

        [Test]
        [Ignore("need to build the latest BLS changes correctly")]
        public void Bls_msg_hash_uncompressed()
        {
            string[] valid = Directory.GetDirectories(Path.Combine("msg_hash_uncompressed", "small"));
            (YamlNode node, YamlNodeType nodeType) = LoadValue(Path.Combine(valid[0], "data.yaml"));
            var input = new {Message = node["input"].Prop<string>("message"), Domain = node["input"].Prop<string>("domain")};
            string[][] outputHex = node.ArrayProp<string[]>("output", sequence => sequence.Children.Select(c => (c as YamlScalarNode).Value).ToArray());
            throw new NotImplementedException();
        }

        [Test]
        public void Bls_priv_to_pub()
        {
            string[] valid = Directory.GetDirectories(Path.Combine("priv_to_pub", "small"));
            foreach (string testCase in valid)
            {
                Console.WriteLine(testCase);
                (YamlNode node, YamlNodeType nodeType) = LoadValue(Path.Combine(testCase, "data.yaml"));
                string inputHex = node.Prop<string>("input");
                byte[] privateKeyBytes = Bytes.FromHexString(inputHex);
                BlsProxy.GetPublicKey(privateKeyBytes, out Span<byte> publicKey);

                string outputHex = node.Prop<string>("output");
                byte[] expectedPublicKey = Bytes.FromHexString(outputHex);
                Assert.AreEqual(expectedPublicKey, publicKey.ToArray());
            }
        }

        [Test]
        public void Bls_sign_msg()
        {
            string[] valid = Directory.GetDirectories(Path.Combine("sign_msg", "small"));
            (YamlNode node, YamlNodeType nodeType) = LoadValue(Path.Combine(valid[0], "data.yaml"));
            var input = new {PrivateKey = node["input"].Prop<string>("privkey"), Message = node["input"].Prop<string>("message"), Domain = node["input"].Prop<string>("domain")};
            string outputHex = node.Prop<string>("output");

            BlsProxy.Sign(out Span<byte> signatureBytes, Bytes.FromHexString(input.PrivateKey), Bytes.FromHexString(input.Message), Bytes.FromHexString(input.Domain));
            Assert.AreEqual(outputHex, signatureBytes.ToHexString(true));
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