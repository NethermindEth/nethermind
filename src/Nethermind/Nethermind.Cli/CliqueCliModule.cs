// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Jint.Native;
using Nethermind.Cli.Modules;
using Nethermind.Core;

namespace Nethermind.Cli
{
    [CliModule("clique")]
    public class CliqueCliModule : CliModuleBase
    {
        [CliFunction("clique", "getSnapshot")]
        public JsValue GetSnapshot()
        {
            return NodeManager.PostJint("clique_getSnapshot").Result;
        }

        [CliFunction("clique", "getSnapshotAtHash")]
        public JsValue GetSnapshotAtHash(string hash)
        {
            return NodeManager.PostJint("clique_getSnapshotAtHash", CliParseHash(hash)).Result;
        }

        [CliFunction("clique", "getSigners")]
        public JsValue GetSigners()
        {
            return NodeManager.PostJint("clique_getSigners").Result;
        }

        [CliFunction("clique", "getSignersAtNumber")]
        public JsValue GetSignersAtNumber(long number)
        {
            return NodeManager.PostJint("clique_getSignersAtNumber", number.ToString()).Result;
        }

        [CliFunction("clique", "getSignersAtHash")]
        public JsValue GetSignersAtHash(string hash)
        {
            return NodeManager.PostJint("clique_getSignersAtHash", CliParseHash(hash)).Result;
        }

        [CliFunction("clique", "getSignersAnnotated")]
        public JsValue GetSignersAnnotated()
        {
            return NodeManager.PostJint("clique_getSignersAnnotated").Result;
        }

        [CliFunction("clique", "getSignersAtHashAnnotated")]
        public JsValue GetSignersAtHashAnnotated(string hash)
        {
            return NodeManager.PostJint("clique_getSignersAtHashAnnotated", CliParseHash(hash)).Result;
        }

        [CliFunction("clique", "propose")]
        public bool Propose(string address, bool vote)
        {
            return NodeManager.Post<bool>("clique_propose", CliParseAddress(address), vote).Result;
        }

        [CliFunction("clique", "discard")]
        public bool Discard(string address)
        {
            return NodeManager.Post<bool>("clique_discard", CliParseAddress(address)).Result;
        }

        [CliFunction("clique", "produceBlock")]
        public bool ProduceBlock(string parentHash)
        {
            return NodeManager.Post<bool>("clique_produceBlock", CliParseHash(parentHash)).Result;
        }

        [CliFunction("clique", "getBlockSigner")]
        public Address? GetBlockSigner(string hash)
        {
            return NodeManager.Post<Address>("clique_getBlockSigner", CliParseHash(hash)).Result;
        }

        public CliqueCliModule(ICliEngine cliEngine, INodeManager nodeManager) : base(cliEngine, nodeManager)
        {
        }
    }
}
