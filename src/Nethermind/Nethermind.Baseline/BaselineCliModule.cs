// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Cli;
using Nethermind.Cli.Modules;

namespace Nethermind.Baseline
{
    [CliModule("baseline")]
    public class BaselineCliModule : CliModuleBase
    {
        public BaselineCliModule(ICliEngine cliEngine, INodeManager nodeManager)
            : base(cliEngine, nodeManager)
        {
        }

        [CliFunction("baseline", "deploy")]
        public string Deploy(string address, string contractType, string? argumentsAbi)
        {
            if (argumentsAbi == null)
            {
                return NodeManager.Post<string>("baseline_deploy", CliParseAddress(address), contractType).Result;
            }

            return NodeManager.Post<string>("baseline_deploy", CliParseAddress(address), contractType, argumentsAbi).Result;
        }


        [CliFunction("baseline", "insertLeaf")]
        public string InsertLeaf(string address, string contractAddress, string hash) =>
            NodeManager.Post<string>(
                "baseline_insertLeaf",
                CliParseAddress(address),
                CliParseAddress(contractAddress),
                CliParseHash(hash)).Result;

        [CliFunction("baseline", "insertLeaves")]
        public string InsertLeaves(string address, string contractAddress, string[] hashes) => NodeManager.Post<string>(
            "baseline_insertLeaves",
            CliParseAddress(address),
            CliParseAddress(contractAddress),
            hashes.Select(CliParseHash).ToArray()
            ).Result;

        [CliFunction("baseline", "getRoot")]
        public string GetRoot(string contractAddress, string? blockParameter) => NodeManager.Post<string>(
            "baseline_getRoot",
            CliParseAddress(contractAddress),
             blockParameter ?? "latest").Result;

        [CliFunction("baseline", "getCount")]
        public string GetCount(string contractAddress, string? blockParameter) => NodeManager.Post<string>(
            "baseline_getCount",
            CliParseAddress(contractAddress),
            blockParameter ?? "latest").Result;

        [CliFunction("baseline", "getLeaf")]
        public object GetLeaf(string contractAddress, long leafIndex, string? blockParameter) => NodeManager.PostJint(
            "baseline_getLeaf",
            CliParseAddress(contractAddress),
            leafIndex,
            blockParameter ?? "latest").Result;

        [CliFunction("baseline", "getLeaves")]
        public object GetLeaves(string contractAddress, long[] leafIndexes, string? blockParameter) => NodeManager.PostJint(
            "baseline_getLeaves",
            CliParseAddress(contractAddress),
            leafIndexes,
            blockParameter ?? "latest").Result;

        [CliFunction("baseline", "getSiblings")]
        public object GetSiblings(string contractAddress, long leafIndex, string? blockParameter) => NodeManager.PostJint(
            "baseline_getSiblings",
            CliParseAddress(contractAddress),
            leafIndex,
            blockParameter ?? "latest").Result;

        [CliFunction("baseline", "verify")]
        public bool Verify(string contractAddress, string root, string leaf, object path, string? blockParameter) => NodeManager.Post<bool>(
            "baseline_verify",
            CliParseAddress(contractAddress),
            CliParseHash(root),
            CliParseHash(leaf),
            path,
            blockParameter ?? "latest").Result;

        [CliFunction("baseline", "verifyAndPush")]
        public bool VerifyAndPush(string address, string contractAddress, object proof, object publicInputs, string commitment) => NodeManager.Post<bool>(
            "baseline_verifyAndPush",
            CliParseAddress(address),
            CliParseAddress(contractAddress),
            proof,
            publicInputs,
            CliParseHash(commitment)).Result;

        [CliFunction("baseline", "track")]
        public string Track(string contractAddress) => NodeManager.Post(
            "baseline_track",
            CliParseAddress(contractAddress)).Result;

        [CliFunction("baseline", "getTracked")]
        public object GetTracked() => NodeManager.PostJint(
            "baseline_getTracked").Result;
    }
}
